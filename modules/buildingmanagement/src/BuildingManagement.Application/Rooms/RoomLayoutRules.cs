using System.Text.Json;
using Volo.Abp;

namespace BuildingManagement.Rooms
{
    public static class RoomLayoutRules
    {
        public const string BoundsError = "InvalidRoomLayout";

        public static void Validate(UpdateRoomLayoutDto input)
        {
            var values = new[] { input.X, input.Y, input.Width, input.Height };
            var any = false;
            var all = true;
            foreach (var value in values)
            {
                if (value.HasValue) any = true; else all = false;
            }

            // Geometry is all-or-nothing: a partially placed room would render
            // at an undefined position on the plan.
            if (any && !all)
                throw new BusinessException(
                    BuildingManagementErrorCodes.InvalidRoomLayout);

            if (all)
            {
                if (input.Width < 0 || input.Height < 0 ||
                    input.Width > 10000 || input.Height > 10000 ||
                    input.X < -10000 || input.X > 10000 ||
                    input.Y < -10000 || input.Y > 10000)
                    throw new BusinessException(
                        BuildingManagementErrorCodes.InvalidRoomLayout);
            }

            if (input.RotationDegrees is < -360 or > 360)
                throw new BusinessException(
                    BuildingManagementErrorCodes.InvalidRoomLayout);

            if (input.Shape == RoomLayoutShape.Polygon)
            {
                if (!all || string.IsNullOrWhiteSpace(input.Points))
                    throw new BusinessException(
                        BuildingManagementErrorCodes.InvalidRoomLayout);
            }

            if (!string.IsNullOrWhiteSpace(input.Points))
            {
                if (input.Points.Length > 4000)
                    throw new BusinessException(
                        BuildingManagementErrorCodes.InvalidRoomLayout);
                try
                {
                    using var document = JsonDocument.Parse(input.Points);
                    if (document.RootElement.ValueKind != JsonValueKind.Array)
                        throw new BusinessException(
                            BuildingManagementErrorCodes.InvalidRoomLayout);
                }
                catch (JsonException)
                {
                    throw new BusinessException(
                        BuildingManagementErrorCodes.InvalidRoomLayout);
                }
            }
        }
    }
}
