# E-AC-3 descriptor bitrate units

Dec3Box.AverageBitrate reports bits per second, consistent with the other audio
sample entries and Mpeg4File.AverageBitrate. The 13-bit data_rate field uses
decimal kilobits: 128 means 128,000 bit/s. Multiplying by 1024 overstates this
value and feeds incorrect source rates into decimal MP3 encoder controls.

Primary implementation corroboration: FFmpeg 8.1 movenc.c, handle_eac3 line 491,
computes data_rate from bit_rate / 1000, and mov_write_eac3_tag writes its 13 bits:
https://ffmpeg.org/doxygen/8.1/movenc_8c_source.html#l00491
This is an interoperability reference, not full ETSI normative admission.

Six structural cases cover zero, one, typical rates and the 13-bit maximum,
unchanged descriptor bytes on save, channel locations and sample rate. Five
nonzero cases fail against the prior multiplier; zero is a control. Full corrected
parser suite passes 175/175. Zero signaling is preserved without inventing a
fallback policy. This change does not add codec/profile support or migrate
persisted application AudioFormat units; that separate legacy contract remains
explicitly open.
