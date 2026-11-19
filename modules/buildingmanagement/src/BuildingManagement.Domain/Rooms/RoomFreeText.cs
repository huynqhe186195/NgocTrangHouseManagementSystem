using System;
using System.Text;

namespace BuildingManagement.Rooms;

/// <summary>
/// Room type and location note are the owner's own words, so the vocabulary is
/// never replaced. What is normalized is the spelling: "Phòng  tiêu  chuẩn" and
/// "Phòng tiêu chuẩn" must not become two types in a grouping or a search, and
/// text typed on another keyboard can arrive decomposed, where "ệ" is two code
/// points and compares unequal to the precomposed one. Case is left exactly as
/// typed — lowercasing would turn "Phòng VIP" into "phòng vip" everywhere.
/// </summary>
public static class RoomFreeText
{
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var collapsed = string.Join(
            ' ',
            value.Normalize(NormalizationForm.FormC)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length == 0 ? null : collapsed;
    }
}
