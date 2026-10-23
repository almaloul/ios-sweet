/*
 * SWEET on QEMU virt: pre-kernel stub (what shim/loader.c's final jump is on
 * the phone). tools/sweet-virt.py loads it at STUB_PA with the kernel image,
 * ADT and boot_args already in place, and fills in `sweet_stub_params`.
 *
 * 1. Point QEMU's ramfb at the framebuffer the kernel draws into
 *    (fw_cfg file "etc/ramfb", written by fw_cfg DMA), so the QEMU window
 *    shows exactly what the phone panel would, with no kernel display driver.
 * 2. Jump to the kernel entry with x0 = boot_args, MMU and caches still off.
 *
 * EL1, MMU off: all memory is Device memory, so build with -mstrict-align
 * and -mgeneral-regs-only (FP is not enabled yet).
 */
#include <stdint.h>

#define UART            0x09000000ul
#define FWCFG           0x09020000ul    /* virt: data +0, selector +8, DMA +16 */
#define FWCFG_FILE_DIR  0x19
#define FWCFG_DMA_ERROR 0x01
#define FWCFG_DMA_SELECT 0x08
#define FWCFG_DMA_WRITE 0x10
#define DRM_XRGB8888    0x34325258u     /* 'XR24': a8r8g8b8 little endian */

struct sweet_stub_params {
	uint64_t boot_args_pa;
	uint64_t entry_pa;
	uint64_t fb_pa;
	uint32_t fb_width, fb_height, fb_stride;
	uint32_t flags;                 /* bit 0: set up ramfb */
};

__attribute__((section(".params"), used))
volatile struct sweet_stub_params sweet_stub_params;

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

static uint16_t
bswap16(uint16_t v)
{
	return (uint16_t)((v >> 8) | (v << 8));
}

static uint32_t
bswap32(uint32_t v)
{
	return __builtin_bswap32(v);
}

static uint64_t
bswap64(uint64_t v)
{
	return __builtin_bswap64(v);
}

static uint8_t
fw_rd8(void)
{
	return *(volatile uint8_t *)FWCFG;
}

static uint32_t
fw_rd_be32(void)
{
	uint32_t v = 0;
	for (int i = 0; i < 4; i++) {
		v = (v << 8) | fw_rd8();
	}
	return v;
}

/* fw_cfg selector key of the file called name, 0 if missing. */
static uint16_t
fw_find(const char *name)
{
	*(volatile uint16_t *)(FWCFG + 8) = bswap16(FWCFG_FILE_DIR);
	uint32_t count = fw_rd_be32();
	for (uint32_t i = 0; i < count && i < 1024; i++) {
		char n[56];
		(void)fw_rd_be32();                     /* size */
		uint16_t select = (uint16_t)((fw_rd8() << 8) | fw_rd8());
		(void)fw_rd8();                         /* reserved */
		(void)fw_rd8();
		for (int j = 0; j < 56; j++) {
			n[j] = (char)fw_rd8();
		}
		int k = 0;
		while (name[k] && n[k] == name[k]) {
			k++;
		}
		if (!name[k] && !n[k]) {
			return select;
		}
	}
	return 0;
}

struct fw_dma {
	uint32_t control, length;
	uint64_t address;
} __attribute__((aligned(16)));

/* QEMU hw/display/ramfb.c RAMFBCfg, big endian, packed. */
struct ramfb_cfg {
	uint64_t addr;
	uint32_t fourcc, flags, width, height, stride;
} __attribute__((packed, aligned(16)));

static struct fw_dma dma_cmd;
static struct ramfb_cfg ramfb;

static int
ramfb_setup(const volatile struct sweet_stub_params *p)
{
	uint16_t select = fw_find("etc/ramfb");
	if (!select) {
		return -1;
	}
	ramfb.addr = bswap64(p->fb_pa);
	ramfb.fourcc = bswap32(DRM_XRGB8888);
	ramfb.flags = 0;
	ramfb.width = bswap32(p->fb_width);
	ramfb.height = bswap32(p->fb_height);
	ramfb.stride = bswap32(p->fb_stride);
	dma_cmd.control = bswap32(((uint32_t)select << 16) | FWCFG_DMA_SELECT | FWCFG_DMA_WRITE);
	dma_cmd.length = bswap32(28);              /* sizeof(RAMFBCfg); ours is padded to 32 */
	dma_cmd.address = bswap64((uint64_t)(uintptr_t)&ramfb);
	__asm__ volatile ("dsb sy" ::: "memory");
	*(volatile uint64_t *)(FWCFG + 16) = bswap64((uint64_t)(uintptr_t)&dma_cmd);
	__asm__ volatile ("dsb sy" ::: "memory");
	for (int i = 0; i < 1000000; i++) {
		uint32_t c = bswap32(*(volatile uint32_t *)&dma_cmd.control);
		if ((c & ~FWCFG_DMA_ERROR) == 0) {
			return (c & FWCFG_DMA_ERROR) ? -2 : 0;
		}
	}
	return -3;
}

__attribute__((noreturn)) void
stub_main(void)
{
	const volatile struct sweet_stub_params *p = &sweet_stub_params;

	if (p->flags & 1u) {
		int r = ramfb_setup(p);
		puts_(r == 0 ? "SWEET STUB: ramfb on\n" :
		    r == -1 ? "SWEET STUB: no ramfb (add -device ramfb)\n" :
		    "SWEET STUB: ramfb fw_cfg DMA failed\n");
	}
	register uint64_t x0 __asm__("x0") = p->boot_args_pa;
	register uint64_t x1 __asm__("x1") = p->entry_pa;
	__asm__ volatile ("br %1" :: "r"(x0), "r"(x1));
	__builtin_unreachable();
}
