namespace BuildingManagement.Floors
{
    public enum FloorLayoutItemKind
    {
        Corridor = 1,
        Staircase = 2,
        Elevator = 3,
        ServiceArea = 4,
        Restroom = 5,
        Entrance = 6,
        Label = 7,
        Laundry = 8,
        Storage = 9,
        Balcony = 10,
        Tree = 11,
        /// <summary>
        /// A wall the owner draws themselves and stretches to any length. The
        /// mockup's storey outline is this object, not something derived from
        /// the rooms: it can be dragged, resized and left open at either end,
        /// which a generated outline around the rooms never can.
        /// </summary>
        Wall = 12,

        /// <summary>
        /// Furniture below is placed on the same plan as the architecture: the
        /// mockup draws a bed, a wardrobe and a desk inside every room, and
        /// those are footprints the owner positions, not something inferred
        /// from the equipment list — a room's inventory says what it holds,
        /// never where it stands.
        /// </summary>
        Bed = 13,

        Wardrobe = 14,

        Desk = 15,

        Chair = 16,

        Sofa = 17,

        AirConditioner = 18,

        Kitchen = 19,

        Refrigerator = 20,

        Television = 21
    }
}
