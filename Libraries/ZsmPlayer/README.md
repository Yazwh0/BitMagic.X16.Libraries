# ZSM Player

The ZSM Player is a ZSM playback routine that is optimised for size and speed. It currently supports PSG, YM and the extended commands. It does not currently support PCM. Extended command support is there in that they can occur in the ZSM file, but will be ignored.

The timing of the playback is up to the caller. It is recommended that `tick` is called once a frame for a 60/s playback.

## Versions

There are two versions of the player, `zsmplayer.bmasm` which is the `import` which should be used for a standard ZSM file. `zsmcompplayer.bmasm` is for a custom 'compressed' version of the ZSM format.

The two playback routines are not compatible, but do share similar signature when it comes to defining their playback code characteristics. *(Note: This will be unified later.)*

## How to generate the player

By using C# commands we can build a custom ZSM Player for the tracks that we want to play back.

We need to know if the tracks use PSG, YM and Extended Commands. If a track has any of these features but the code isn't enabled then the playback will crash. We do this to minimise the size of the playback code. Why test and handle for YM playback if the track is only PSG?!

The following code shows how to build code that

``` c#
ZsmPlayer.Create()
         .WithPsg()                ; to enable PSG code
         .WithYm()                 ; to enable YM code
         .WithExtCommands()        ; to enable code for the extended commands
         .UseZp()                  ; to use 4 bytes in the ZP making the main code smaller and faster
         .UseRamBank(zsmRamBank)   ; set the Ram Bank that the ZSM track will be loaded into
         .Build();                 ; build the code
```

`.UseRamBank` sets the bank directly within the code, saving a handful of CPU cycles. If this is not set it will use the curerent ram bank when `init_player` is called.

And then later where you want the playback code to go:

``` c#
ZsmPlayer.Generate();
```

## How to use the player

The code will be in the *ZsmPlayer* scope.

ZsmPlayer:**init_player**

This must be called to initialise the player. If `UseRamBank` isn't set, the current ram bank will be used for the ZSM File.

ZsmPlayer:**tick**

Reads one line from the ZSM file and plays it. Ideally called once a frame.

An example of how to use the player is within the [../../LibraryDev/ZsmPlayer](example workspace).

## ZSM Compress

The second playback routine `zsmcompplayer.bmasm` uses a special form of ZSM files. Its a 'compressed' format where each line within the playback is de-duplicated, and then a list of lines for the order of playback.

### Compression

There are two ways to compress the file, either directly in your `bmasm` file as follows:

``` c#
var zsmCompFile = ZsmCompressor.Compress(File.ReadAllBytes(@"../AUDIO.BIN"), zsmRamBank, 0xa000, out var dictionarySize, out var dataSize);
File.WriteAllBytes(@"../AUDCOMP.BIN", zsmCompFile);
```

Or via the command line application in the [../../Apps/ZsmCompress](Apps\ZsmCompress) folder.

The file generated is not a general purpose file as the address of each line is stored within. This is to improve performance, as the location of where the player needs to look is built into the data stream. It is vital that the address and bank passed into the compressor is the same as the ZsmPlayer that is built.

## Code

In its smallest form, that only handles PSG, the compressed playback code is 162 bytes.

The code and the way it is built is to optimise for the smallest code size as we do not include features that we do not need. This is why the library is only available as code, not as a object file or other means of sharing the binary.

## License

The playback code is licensed under the MIT License. See the license file for more infor.
