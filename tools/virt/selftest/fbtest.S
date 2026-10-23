	.section .text.start
	.globl _start
_start:
	ldr	x1, =0x100200000
	mov	sp, x1
	b	fbtest_main
