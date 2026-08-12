namespace Mpeg4Lib.Boxes.EC3SpecificBox;

/// <summary>
/// ETSI TS 102 366 Table F.6.1 chan_loc field bit indices. Explicit values
/// preserve the public enum's numeric ABI; they are indices, not bit masks.
/// </summary>
public enum ChannelLocation : short
{
	Lc_Rc_Pair = 0,
	Lrs_Rrs_Pair = 1,
	Cs = 2,
	Ts = 3,
	Lsd_Rsd_Pair = 4,
	Lw_Rw_Pair = 5,
	Lvh_Rvh_Pair = 6,
	Cvh = 7,
	LFE2 = 8,
}
