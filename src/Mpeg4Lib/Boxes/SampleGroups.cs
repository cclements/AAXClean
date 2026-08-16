using System;
using System.Linq;

namespace Mpeg4Lib.Boxes;

//ISO/IEC 23001-7 §§ 5-6: 'seig' sample-group descriptions (sgpd) and sample-to-group
//mappings (sbgp) can override the track-level tenc protection state and key identity
//for arbitrary sample runs. This library does not implement those overrides, so
//protected content that carries them must be rejected before any sample is decrypted
//with the track defaults and written out as if it were correct.
public static class SampleGroups
{
	public static bool ContainsCencSampleGroup(IBox box)
	{
		if (box.Header.Type is "sgpd" or "sbgp")
		{
			//Sample-group boxes have no typed parser today, so they surface as
			//UnknownBox and the grouping type is read from the raw payload (the four
			//bytes after version/flags). If a typed SgpdBox/SbgpBox is ever added,
			//this detector must be taught to read its grouping type; until then it
			//fails closed on the typed shape rather than silently reporting "no
			//seig" and letting a seig-overridden fragment decrypt under the default
			//KID.
			if (box is not UnknownBox unknown)
				return true;

			return unknown.Data.Length >= 8
				&& unknown.Data.AsSpan(4, 4).SequenceEqual("seig"u8);
		}

		return box.Children.Any(ContainsCencSampleGroup);
	}
}
