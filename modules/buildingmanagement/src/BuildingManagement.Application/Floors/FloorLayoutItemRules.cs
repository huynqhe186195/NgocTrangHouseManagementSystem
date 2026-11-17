using System;
using System.Text.Json;
using Volo.Abp;

namespace BuildingManagement.Floors
{
    /// <summary>
    /// Rules for a floor object's geometry. Split out of <see cref="FloorAppService"/>
    /// so the polyline format can be checked without standing up the whole
    /// application service, the same way <c>RoomLayoutRules</c> covers a room.
    /// </summary>
    public static class FloorLayoutItemRules
    {
        public const int MinPolylinePoints = 2;
        public const int MaxPolylinePoints = 64;
        public const int MaxPolylineLength = 4000;
        public const decimal MaxThickness = 2m;
        public const decimal MaxCornerRadius = 5m;

        /// <summary>Blank means "no centreline", so it is stored as null.</summary>
        public static string? NormalizePoints(string? points)
        {
            return string.IsNullOrWhiteSpace(points) ? null : points.Trim();
        }

        public static void Validate(
            FloorLayoutItemKind kind,
            decimal x, decimal y, decimal width, decimal height,
            string? points, decimal? thickness, decimal? cornerRadius)
        {
            if (!Enum.IsDefined(kind) ||
                width < 0 || height < 0 ||
                width > 10000 || height > 10000 ||
                x < -10000 || x > 10000 || y < -10000 || y > 10000)
                throw new BusinessException(
                    BuildingManagementErrorCodes.InvalidFloorLayoutItem);

            var hasPolyline = !string.IsNullOrWhiteSpace(points);
            var hasWallShape = hasPolyline ||
                thickness.HasValue || cornerRadius.HasValue;

            // Only a wall has a centreline and a thickness. Anything else that
            // sends them is a client bug, and accepting it would store geometry
            // the plan can never draw.
            if (hasWallShape && kind != FloorLayoutItemKind.Wall)
                throw new BusinessException(
                    BuildingManagementErrorCodes.InvalidFloorLayoutItemPoints);

            if (thickness is < 0 or > MaxThickness ||
                cornerRadius is < 0 or > MaxCornerRadius)
                throw new BusinessException(
                    BuildingManagementErrorCodes.InvalidFloorLayoutItemPoints);

            if (hasPolyline)
                ValidatePolyline(points!);
        }

        private static void ValidatePolyline(string points)
        {
            if (points.Length > MaxPolylineLength)
                throw new BusinessException(
                    BuildingManagementErrorCodes.InvalidFloorLayoutItemPoints);

            try
            {
                using var document = JsonDocument.Parse(points);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                    throw new BusinessException(
                        BuildingManagementErrorCodes.InvalidFloorLayoutItemPoints);

                var count = 0;
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    count++;
                    if (count > MaxPolylinePoints ||
                        element.ValueKind != JsonValueKind.Object ||
                        !element.TryGetProperty("x", out var px) ||
                        !element.TryGetProperty("y", out var py) ||
                        px.ValueKind != JsonValueKind.Number ||
                        py.ValueKind != JsonValueKind.Number ||
                        px.GetDecimal() is < -10000 or > 10000 ||
                        py.GetDecimal() is < -10000 or > 10000)
                        throw new BusinessException(
                            BuildingManagementErrorCodes.InvalidFloorLayoutItemPoints);
                }

                if (count < MinPolylinePoints)
                    throw new BusinessException(
                        BuildingManagementErrorCodes.InvalidFloorLayoutItemPoints);
            }
            catch (JsonException)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes.InvalidFloorLayoutItemPoints);
            }
        }
    }
}
