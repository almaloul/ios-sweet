/* Loaded at STUB_PA (tools/sweet-virt.py). Params at a fixed offset. */
ENTRY(_start)
SECTIONS {
	. = 0x40200000;
	.text : { *(.text.start) *(.text*) *(.rodata*) }
	. = 0x40200f00;
	.params : { KEEP(*(.params)) }
	.data : { *(.data*) }
	.bss : { *(.bss*) *(COMMON) }
	. = ALIGN(16) + 0x4000;
	__stack_top = .;
}
