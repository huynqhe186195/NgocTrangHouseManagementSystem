namespace BuildingManagement.Floors
{
    /// <summary>
    /// The building-management mockup shows a status badge on every floor row.
    /// The stored values match the wording the screen uses: a floor is either
    /// in service, temporarily closed for work, or retired.
    /// </summary>
    public enum FloorStatus
    {
        Active = 1,
        Maintenance = 2,
        Inactive = 3
    }
}
