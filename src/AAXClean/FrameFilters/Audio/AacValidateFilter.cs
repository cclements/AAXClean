using System;

namespace AAXClean.FrameFilters.Audio
{
	internal class AacValidateFilter : FrameTransformBase<FrameEntry, FrameEntry>
	{
		protected override int InputBufferSize => 1000;

		private readonly bool deriveUsacSync;

		public AacValidateFilter(bool audioIsUsac = false)
			=> deriveUsacSync = audioIsUsac;

		public override FrameEntry PerformFiltering(FrameEntry input)
		{
			if (!ValidateFrame(input.FrameData.Span))
				throw new Exception("Aac error!");

			//For USAC the bitstream is the ground truth for sync samples: the first bit of
			//every access unit is usacIndependencyFlag. Source tables may be absent or wrong
			//(the very defect the stss writer repairs), so any metadata-derived value is
			//overridden here — after decryption, at the first point the cleartext bitstream
			//is available.
			if (deriveUsacSync)
				input.IsSyncSample = !input.FrameData.IsEmpty && (input.FrameData.Span[0] & 0x80) != 0;

			return input;
		}

		private static bool ValidateFrame(ReadOnlySpan<byte> frame) => (AV_RB16(frame) & 0xfff0) != 0xfff0;

		//Defined at
		//http://man.hubwiz.com/docset/FFmpeg.docset/Contents/Resources/Documents/api/intreadwrite_8h_source.html
		private static ushort AV_RB16(ReadOnlySpan<byte> frame)
		{
			return (ushort)(frame[0] << 8 | frame[1]);
		}
	}
}
