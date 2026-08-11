using System;
using System.IO;

namespace Mpeg4Lib.Boxes.EC3SpecificBox;

public static class Ec3Extensions
{
	public static int GetSampleRate(this Ec3IndependentSubstream ind_sub)
		=> ind_sub.fscod == 0 ? 48000
		: ind_sub.fscod == 1 ? 44100
		: ind_sub.fscod == 2 ? 32000
		: throw new InvalidDataException($"{nameof(ind_sub.fscod)} value of {ind_sub.fscod} is not valid");

	public static int ChannelCount(this Ec3IndependentSubstream ind_sub)
	{
		int channels = ff_ac3_channels_tab[(byte)ind_sub.acmod] + (ind_sub.lfeon ? 1 : 0);
		if (ind_sub.num_dep_sub == 0)
			return channels;

		channels += ind_sub.HasChannelLocation(ChannelLocation.Lc_Rc_Pair) ? 2 : 0;
		channels += ind_sub.HasChannelLocation(ChannelLocation.Lrs_Rrs_Pair) ? 2 : 0;
		channels += ind_sub.HasChannelLocation(ChannelLocation.Cs) ? 1 : 0;
		channels += ind_sub.HasChannelLocation(ChannelLocation.Ts) ? 1 : 0;
		channels += ind_sub.HasChannelLocation(ChannelLocation.Lsd_Rsd_Pair) ? 2 : 0;
		channels += ind_sub.HasChannelLocation(ChannelLocation.Lw_Rw_Pair) ? 2 : 0;
		channels += ind_sub.HasChannelLocation(ChannelLocation.Lvh_Rvh_Pair) ? 2 : 0;
		channels += ind_sub.HasChannelLocation(ChannelLocation.Cvh) ? 1 : 0;
		channels += ind_sub.HasChannelLocation(ChannelLocation.LFE2) ? 1 : 0;
		return channels;
	}

	/// <summary>
	/// Test a legacy channel-location ordinal against the raw nine-bit chan_loc field
	/// retained in <see cref="Ec3IndependentSubstream.chan_loc"/>.
	/// </summary>
	public static bool HasChannelLocation(this Ec3IndependentSubstream ind_sub, ChannelLocation location)
	{
		ArgumentNullException.ThrowIfNull(ind_sub);
		int ordinal = (int)location;
		if ((uint)ordinal > 8)
			throw new ArgumentOutOfRangeException(nameof(location));
		int rawMask = (int)ind_sub.chan_loc;
		return (rawMask & (1 << (8 - ordinal))) != 0;
	}

	/// <summary>
	/// ETSI TS 102 366 4.4.2.3 Table 4.3: Audio coding mode column 4 (Nfchans)
	/// </summary>
	private static readonly byte[] ff_ac3_channels_tab = [2, 1, 2, 3, 3, 4, 4, 5];
}
