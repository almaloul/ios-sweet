/*
 * Stand-in "kernel" for tools/virt/selftest/launcher_test.py: entered by the
 * ramfb stub like XNU (x0 = boot_args), it paints the framebuffer named in
 * boot_args (v_baseAddr/rowBytes/width/height) with three horizontal bands
 * and a white square in the middle, then waits.
 */
#include <stdint.h>
#include <stdbool.h>

void
fbtest_main(uint64_t boot_args)
{
	volatile uint64_t *ba = (volatile uint64_t *)boot_args;
	uint64_t base = ba[5], row = ba[7], w = ba[8], h = ba[9];
	for (uint64_t y = 0; y < h; y++) {
		volatile uint32_t *line = (volatile uint32_t *)(base + y * row);
		uint32_t c = y < h / 3 ? 0x00ff0000u : y < 2 * h / 3 ? 0x0000ff00u : 0x000000ffu;
		for (uint64_t x = 0; x < w; x++) {
			bool sq = x >= w / 2 - 50 && x < w / 2 + 50 && y >= h / 2 - 50 && y < h / 2 + 50;
			line[x] = sq ? 0x00ffffffu : c;
		}
	}
	for (;;) {
		__asm__ volatile ("wfi");
	}
}
