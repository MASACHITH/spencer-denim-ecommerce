namespace SpencerDenim.Domain.Analytics;

/// <summary>
/// <c>analytics.PageViews.Channel</c> (doc 08 §3.8: "1 Direct, 2 Organic, 3 Social, 4 Referral, 5 Email,
/// 6 Paid, 7 Other"), the same seven doc 11 P16 names for the Traffic Sources widget. The rules that pick one
/// are in <c>TrafficSourceClassifier</c>.
/// </summary>
public enum TrafficChannel : byte
{
    Direct = 1,
    Organic = 2,
    Social = 3,
    Referral = 4,
    Email = 5,
    Paid = 6,
    Other = 7,
}
