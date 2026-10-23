	.section .text.start
	.globl _start
_start:
	msr	daifset, #0xf
	ldr	x0, =__stack_top
	mov	sp, x0
	b	stub_main
