/* EL1, MMU off. Zero .bss, set a stack, run selftest_main. */
	.section .text.start
	.globl _start
_start:
	msr	daifset, #0xf
	ldr	x0, =__bss_start
	ldr	x1, =__bss_end
1:	cmp	x0, x1
	b.hs	2f
	str	xzr, [x0], #8
	b	1b
2:	ldr	x0, =__stack_top
	mov	sp, x0
	bl	selftest_main
3:	wfi
	b	3b
