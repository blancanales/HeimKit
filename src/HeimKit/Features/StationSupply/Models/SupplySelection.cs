namespace HeimKit.Features.StationSupply.Models;

internal struct SupplySelection
{
    internal ZDO? Selected, NearestRejected;
    internal string? Rejection;
    internal bool Checked, CountsMeasured;
    internal int Eligible, Ineligible, WrongSide, WrongHeight, OutOfRange;
}
