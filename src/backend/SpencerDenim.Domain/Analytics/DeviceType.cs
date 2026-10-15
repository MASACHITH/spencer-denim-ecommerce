namespace SpencerDenim.Domain.Analytics;

/// <summary><c>analytics.PageViews.DeviceType</c> (doc 08 §3.8: "1 Desktop, 2 Mobile, 3 Tablet, 9 Other").</summary>
public enum DeviceType : byte
{
    Desktop = 1,
    Mobile = 2,
    Tablet = 3,
    Other = 9,
}
