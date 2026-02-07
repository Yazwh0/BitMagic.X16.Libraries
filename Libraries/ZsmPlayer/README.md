# ZSM Tiny and ZSM Tinier

The ZSM Tiny is a ZSM playback routine that is optimized for size and speed. It currently supports PSG, YM, and the extended commands. It does not currently support PCM. Extended command support is there in that they can occur in the ZSM file, but will be ignored.

The timing of the playback is up to the caller. It is recommended that `tick` is called once a frame for 60 fps playback.

## Versions

There are two versions of the player, _Tiny_ and _Tinier_:
- `zsmtiny.bmasm`: The standard import for a standard ZSM file.
- `zsmtinier.bmasm`: For a custom 'compressed' version of the ZSM format.

The two playback routines are not compatible, but share the same signature when defining their playback code characteristics.

## Examples

In this repository there are two testing files in the  'library development' folder.

The `project.json` will need to be altered to run each one. The filename and optionally the autoboot file need to be changed

Either `src/tiny.bmasm` and `TINY.PRG` or `src/tinier.bmasm` and `TINIER.PRG`.

## How to Generate the Player

By using C# commands, you can build a custom ZSM Player for the tracks that we want to play back.

The playback generated needs to know if the tracks use PSG, YM, and Extended Commands. If a track has any of these features but the code isn't enabled, then the playback will crash. We do this to minimize the size of the playback code. Why test and handle for YM playback if the track is only PSG?!

The following code shows the possible options to build the code with. It is possible to omit any of the calls to produce code that is tailored to your requirements.

```c#
ZsmPlayer.Create()
         .WithPsg()                // to enable PSG code
         .WithYm()                 // to enable YM code
         .WithExtCommands()        // to enable code for the extended commands
         .UseZp()                  // to use 4 bytes in the ZP making the main code smaller and faster
         .UseRamBank(zsmRamBank)   // set the RAM Bank that the ZSM track will be loaded into
         .Build();                 // build the code
```

`.UseRamBank` sets the bank directly within the code, saving a handful of CPU cycles. If this is not set, it will use the current RAM bank when `init_player` is called.

And then to place the generated code use:

```c#
ZsmPlayer.Generate();
```

## How to Use the Player

The code will be in the *ZsmPlayer* scope.

**ZsmPlayer:init_player**

This must be called to initialise the player. If `UseRamBank` isn't set, the current RAM bank will be used for the ZSM File.

**ZsmPlayer:tick**

Reads one line from the ZSM file and plays it. Ideally called once a frame.

An example of how to use the player is within the [example workspace](../../LibraryDev/ZsmPlayer).

## ZSM Compress

The second playback routine `zsmcompplayer.bmasm` uses a special form of ZSM files. It's a 'compressed' format where each line within the playback is de-duplicated, and then a list of lines for the order of playback.

### Compression

There are two ways to compress the file, either directly in your `bmasm` file as follows:

```c#
var zsmCompFile = ZsmCompressor.Compress(File.ReadAllBytes(@"../AUDIO.BIN"), zsmRamBank, 0xa000, out var dictionarySize, out var dataSize);
File.WriteAllBytes(@"../AUDCOMP.BIN", zsmCompFile);
```

Or via the command line application in the [Apps\ZsmCompress](../../Apps/ZsmCompress) folder.

The file generated is not a general-purpose file as the address of each line is stored within. This is to improve performance, as the location of where the player needs to look is built into the data stream. It is vital that the address and bank passed into the compressor is the same as the ZsmPlayer that is built.

## Code

In its smallest form, that only handles PSG and utilises ZP, the compressed playback code is 163 bytes. The standard player is only 127 bytes.

### Size vs Options

Player and the player size change in bytes per option. The base size is just with PSG and RAM bank not specified.

| Version | Size | ZP | RamBank | YM | Ext | Min Size | Max Size |
| ------- | ---- | -- | ------- | -- | --- | -------- | -------- |
| Tinier | 198 bytes | -18 | -17 | +30 | +34 | 163 bytes | 262 bytes |
| Tiny | 143 bytes | 0 | -17 | +30 | +34 | 126 bytes | 241 bytes |

The way the code is built is to optimize for the smallest code size by ensuring we do not include features that are not needed. This is why the library is only available as code, not as an object file or other means of sharing the binary.

### Example

The code generated for a player that supports PSG only, Ram Bank 2 and makes use of 4 bytes in the ZP to minimise size is as below.

The generated code can always be viewed via setting `saveGeneratedBmasm` to true in the `project.json`.

``` asm
.segment ZP scope:ZsmPlayer
.padvar ushort data_pointer
.padvar ushort line_pointer
.endsegment

.scope ZsmPlayer
.constvar uint psg_start = 0x1f9c0

.proc get_byte
    inc data_pointer
    bne +skip
    inc data_pointer + 1
    lda data_pointer + 1
    cmp #$c0
    bne +skip
    inc RAM_BANK
    lda #$a0
    sta data_pointer + 1
    stz data_pointer

.skip:
    lda (data_pointer)
    rts
.endproc

.proc get_line_byte
    inc line_pointer
    bne +skip
    inc line_pointer + 1
    lda line_pointer + 1
    cmp #$c0
    bne +skip
    inc RAM_BANK
    lda #$a0
    sta line_pointer + 1
    stz line_pointer

.skip:
    lda (line_pointer)
    rts
.endproc

.proc tick
    lda countdown: #$00
    beq tick_setup
    dec countdown
    rts

.tick_setup:
    lda line_bank: #$ab
    sta RAM_BANK
    jsr get_line_byte
    sta data_pointer
    jsr get_line_byte
    sta data_pointer + 1
    jsr get_line_byte
    ldx RAM_BANK
    stx line_bank
    sta RAM_BANK
    ldy ADDRx_H
    sty addr_h
    ldy ADDRx_M
    sty addr_m
    ldy ADDRx_L
    sty addr_l
    ldy #^psg_start
    sty ADDRx_H
    ldy #>psg_start
    sty ADDRx_M

.tick_loop:
    jsr get_byte
    bmi exit

.vera:
    clc
    adc #<psg_start
    sta ADDRx_L
    jsr get_byte
    sta DATA0
    bra -tick_loop

.ext_or_ym:

.exit:
    ldy addr_h: #$ab
    sty ADDRx_H
    ldy addr_m: #$ab
    sty ADDRx_M
    ldy addr_l: #$ab
    sty ADDRx_L
    and #$7f
    beq all_done
    dec
    sta countdown

.step_done:
    rts

.all_done:
.endproc

.proc init_player
    lda #2
    sta tick:line_bank
    lda #<$a000-1
    sta line_pointer
    lda #>$a000-1
    sta line_pointer + 1
    rts
.endproc

.endscope
```

## License

The playback code is licensed under the MIT License. See the license file for more info.
