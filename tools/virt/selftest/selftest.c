/*
 * Bare-metal self-test for bsd/dev/sweet_virtio_core.h on QEMU virt.
 *
 * Runs at EL1 with the MMU off (so everything is Device memory: build with
 * -mstrict-align). Prints over the PL011 at 0x09000000 and powers off with
 * PSCI SYSTEM_OFF. Driven by tools/virt/selftest/run.py, which provides a
 * patterned disk image and injects input events over QMP.
 *
 * Checks:
 *   blk    read sectors 0-7, compare against the pattern, write sector 9,
 *          flush, read it back
 *   input  tablet + keyboard: report touches and keys until the host's
 *          script has finished (key 'q') or a timeout
 */
#include <stdint.h>
#include <stdbool.h>
#include "sweet_virtio_core.h"

#define UART            0x09000000ul
#define VIRTIO_BASE     0x0a000000ul
#define PANEL_W         1080
#define PANEL_H         2400

/* Static, 16 KiB-aligned DMA area (MMU off: VA == PA). */
static uint8_t dma[64 * 1024] __attribute__((aligned(16384)));
static uint8_t blk_data[8 * 512] __attribute__((aligned(64)));

static void
putc_(char c)
{
	volatile uint32_t *u = (volatile uint32_t *)UART;
	while (u[0x18 / 4] & (1u << 5)) {
	}
	u[0] = (uint32_t)c;
}

static void
puts_(const char *s)
{
	while (*s) {
		if (*s == '\n') {
			putc_('\r');
		}
		putc_(*s++);
	}
}

static void
putx(uint64_t v)
{
	char b[19];
	b[0] = '0';
	b[1] = 'x';
	int n = 2;
	bool started = false;
	for (int i = 60; i >= 0; i -= 4) {
		unsigned d = (v >> i) & 0xf;
		if (d || started || i == 0) {
			b[n++] = "0123456789abcdef"[d];
			started = true;
		}
	}
	b[n] = 0;
	puts_(b);
}

static void
putd(int64_t v)
{
	char b[24];
	int n = 0;
	if (v < 0) {
		putc_('-');
		v = -v;
	}
	do {
		b[n++] = (char)('0' + v % 10);
		v /= 10;
	} while (v);
	while (n) {
		putc_(b[--n]);
	}
}

static uint64_t
ticks(void)
{
	uint64_t v;
	__asm__ volatile ("isb; mrs %0, cntvct_el0" : "=r"(v));
	return v;
}

static uint64_t
freq(void)
{
	uint64_t v;
	__asm__ volatile ("mrs %0, cntfrq_el0" : "=r"(v));
	return v;
}

void *
memset(void *p, int c, unsigned long n)
{
	volatile uint8_t *b = p;
	while (n--) {
		*b++ = (uint8_t)c;
	}
	return p;
}

void *
memcpy(void *dst, const void *src, unsigned long n)
{
	volatile uint8_t *d = dst;
	const volatile uint8_t *s = src;
	while (n--) {
		*d++ = *s++;
	}
	return dst;
}

static void
bzero_(void *p, unsigned n)
{
	volatile uint8_t *b = p;
	while (n--) {
		*b++ = 0;
	}
}

static __attribute__((noreturn)) void
poweroff(void)
{
	register uint64_t x0 __asm__("x0") = 0x84000008ul;     /* PSCI SYSTEM_OFF */
	__asm__ volatile ("hvc #0" : "+r"(x0) :: "memory");
	for (;;) {
	}
}

static int failures;

static void
check(bool ok, const char *what)
{
	puts_(ok ? "  ok   " : "  FAIL ");
	puts_(what);
	puts_("\n");
	failures += !ok;
}

/* Expected disk pattern: byte i of sector s = (s * 7 + i) & 0xff. */
static bool
pattern_ok(const uint8_t *buf, uint64_t first, unsigned sectors)
{
	for (unsigned s = 0; s < sectors; s++) {
		for (unsigned i = 0; i < 512; i++) {
			if (buf[s * 512 + i] != (uint8_t)((first + s) * 7 + i)) {
				return false;
			}
		}
	}
	return true;
}

static int
blk_io(struct svio_dev *d, struct svio_queue *q, struct svio_blk_hdr *hdr,
    uint32_t type, uint64_t sector, void *buf, uint32_t len)
{
	svio_blk_start(d, q, hdr, (uint64_t)(uintptr_t)hdr, type, sector,
	    (uint64_t)(uintptr_t)buf, len);
	uint64_t deadline = ticks() + freq() * 5;
	int st;
	while ((st = svio_blk_done(d, q, hdr)) < 0) {
		if (ticks() > deadline) {
			return -2;
		}
	}
	return st;
}

static void
test_blk(uintptr_t regs)
{
	struct svio_dev d = { .regs = regs };
	struct svio_queue q;
	uint8_t *qmem = dma;                            /* 0x0000 queue */
	struct svio_blk_hdr *hdr = (void *)(dma + 0x2000);

	puts_("blk:\n");
	check(svio_init(&d, SVIO_BLK_F_RO | SVIO_BLK_F_FLUSH) == 0, "negotiate (VERSION_1)");
	uint64_t cap = svio_cfg64(&d, 0);
	puts_("  capacity sectors ");
	putd((int64_t)cap);
	puts_(", features ");
	putx(d.features);
	puts_("\n");
	bzero_(qmem, 0x2000);
	check(svio_queue_setup(&d, &q, 0, 8, qmem, (uint64_t)(uintptr_t)qmem) == 0, "queue 0 setup");
	svio_driver_ok(&d);

	bzero_(blk_data, sizeof(blk_data));
	int st = blk_io(&d, &q, hdr, SVIO_BLK_T_IN, 0, blk_data, sizeof(blk_data));
	check(st == SVIO_BLK_S_OK, "read sectors 0-7");
	check(pattern_ok(blk_data, 0, 8), "sector 0-7 contents match the pattern");

	st = blk_io(&d, &q, hdr, SVIO_BLK_T_IN, 3, blk_data, 1024);
	check(st == SVIO_BLK_S_OK && pattern_ok(blk_data, 3, 2), "read at sector 3");

	for (unsigned i = 0; i < 512; i++) {
		blk_data[i] = (uint8_t)(0xa5 ^ i);
	}
	st = blk_io(&d, &q, hdr, SVIO_BLK_T_OUT, 9, blk_data, 512);
	check(st == SVIO_BLK_S_OK, "write sector 9");
	if (d.features & SVIO_BLK_F_FLUSH) {
		st = blk_io(&d, &q, hdr, SVIO_BLK_T_FLUSH, 0, 0, 0);
		check(st == SVIO_BLK_S_OK, "flush");
	}
	bzero_(blk_data, 512);
	st = blk_io(&d, &q, hdr, SVIO_BLK_T_IN, 9, blk_data, 512);
	bool same = st == SVIO_BLK_S_OK;
	for (unsigned i = 0; i < 512 && same; i++) {
		same = blk_data[i] == (uint8_t)(0xa5 ^ i);
	}
	check(same, "read back sector 9");

	/* Many back-to-back requests: ring index wraparound past num. */
	bool all = true;
	for (unsigned i = 0; i < 40 && all; i++) {
		all = blk_io(&d, &q, hdr, SVIO_BLK_T_IN, i % 8, blk_data, 512) == 0 &&
		    pattern_ok(blk_data, i % 8, 1);
	}
	check(all, "40 sequential reads (ring wraparound)");
}

/* ---- input ---- */

#define INPUT_DEVS      4
#define EV_BUFS         32

static struct {
	struct svio_dev d;
	struct svio_queue q;
	struct svio_touch t;
	struct svio_input_event *ev;
	char name[48];
} in[INPUT_DEVS];
static int nin;
static int touches_down, touches_up, keys_seen, moves;
static bool quit;

static void
on_touch(int finger, int down, int x, int y)
{
	puts_("  touch finger ");
	putd(finger);
	puts_(down ? " down " : " up ");
	putd(x);
	puts_(",");
	putd(y);
	puts_("\n");
	if (down) {
		touches_down++;
		if (x > 500 && x < 580 && y > 1150 && y < 1250) {
			moves++;        /* center of the panel, as injected by run.py */
		}
	} else {
		touches_up++;
	}
}

static void
on_key(uint16_t code, int down)
{
	puts_("  key ");
	putd(code);
	puts_(down ? " down\n" : " up\n");
	if (down) {
		keys_seen++;
	}
	if (code == 16 /* KEY_Q */ && !down) {
		quit = true;
	}
}

static void
add_input(uintptr_t regs)
{
	if (nin == INPUT_DEVS) {
		return;
	}
	int n = nin++;
	uint8_t *qmem = dma + 0x4000 + (unsigned)n * 0x2000;
	in[n].d.regs = regs;
	in[n].ev = (void *)(qmem + 0x1000);
	bzero_(qmem, 0x2000);
	if (svio_init(&in[n].d, 0) != 0 ||
	    svio_queue_setup(&in[n].d, &in[n].q, 0, EV_BUFS, qmem, (uint64_t)(uintptr_t)qmem) != 0) {
		check(false, "input setup");
		return;
	}
	svio_input_name(&in[n].d, in[n].name, sizeof(in[n].name));
	struct svio_touch *t = &in[n].t;
	t->mt = svio_input_abs(&in[n].d, SVIO_ABS_MT_POSITION_X, &t->xmin, &t->xmax) &&
	    svio_input_abs(&in[n].d, SVIO_ABS_MT_POSITION_Y, &t->ymin, &t->ymax);
	if (!t->mt) {
		(void)svio_input_abs(&in[n].d, SVIO_ABS_X, &t->xmin, &t->xmax);
		(void)svio_input_abs(&in[n].d, SVIO_ABS_Y, &t->ymin, &t->ymax);
	}
	puts_("input: \"");
	puts_(in[n].name);
	puts_("\" x ");
	putd(t->xmin);
	puts_("..");
	putd(t->xmax);
	puts_(" y ");
	putd(t->ymin);
	puts_("..");
	putd(t->ymax);
	puts_(t->mt ? " multitouch\n" : "\n");
	for (uint16_t i = 0; i < EV_BUFS; i++) {
		svio_input_post(&in[n].q, (uint64_t)(uintptr_t)in[n].ev, i);
	}
	svio_driver_ok(&in[n].d);
	svio_notify(&in[n].d, &in[n].q);
}

static void
poll_inputs(void)
{
	for (int n = 0; n < nin; n++) {
		uint32_t id, len;
		bool any = false;
		while (svio_pop(&in[n].q, &id, &len)) {
			if (id < EV_BUFS) {
				struct svio_input_event e = in[n].ev[id];
				svio_touch_event(&in[n].t, &e, PANEL_W, PANEL_H, on_touch, on_key);
				svio_input_post(&in[n].q, (uint64_t)(uintptr_t)in[n].ev, (uint16_t)id);
				any = true;
			}
		}
		if (any) {
			svio_ack(&in[n].d);
			svio_notify(&in[n].d, &in[n].q);
		}
	}
}

void
selftest_main(void)
{
	uintptr_t blk = 0;

	puts_("\nSWEET virtio selftest\n");
	for (unsigned s = 0; s < SVIO_MAX_SLOTS; s++) {
		uint32_t version;
		uintptr_t regs = VIRTIO_BASE + s * SVIO_SLOT_SIZE;
		uint32_t id = svio_probe(regs, &version);
		if (version == 1) {
			puts_("slot ");
			putd(s);
			puts_(": legacy transport (need -global virtio-mmio.force-legacy=false)\n");
		}
		if (!id) {
			continue;
		}
		puts_("slot ");
		putd(s);
		puts_(": device ");
		putd(id);
		puts_("\n");
		if (id == SVIO_DEV_BLK && !blk) {
			blk = regs;
		} else if (id == SVIO_DEV_INPUT) {
			add_input(regs);
		}
	}
	check(blk != 0, "found virtio-blk");
	if (blk) {
		test_blk(blk);
	}
	check(nin >= 2, "found tablet and keyboard");
	puts_("READY FOR INPUT\n");
	uint64_t deadline = ticks() + freq() * 30;
	while (!quit && ticks() < deadline) {
		poll_inputs();
	}
	check(quit, "input script finished (key q)");
	check(touches_down >= 2 && touches_up >= 1, "touch down/up reported");
	check(moves >= 1, "touch position scaled to the panel center");
	check(keys_seen >= 2, "keys reported");
	puts_(failures ? "SELFTEST FAIL\n" : "SELFTEST PASS\n");
	poweroff();
}
