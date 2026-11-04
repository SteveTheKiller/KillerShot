namespace KillerShot.Services
{
    /// <summary>
    /// APP-OWNED: how KillerShot updates on the shared update stack (Services/UpdateService.cs).
    /// The Microsoft Store build is self-contained, and the GitHub release exe needs the .NET
    /// Desktop Runtime, so a Store copy never swaps itself; the Store updates it.
    /// </summary>
    internal static partial class UpdateService
    {
        /// <summary>Resource key of the line the About card shows on a Store build.</summary>
        internal const string StoreChannelMessageKey = "Str_Update_StoreChannel";

        /// <summary>True for the self-contained Microsoft Store build (STORE_BUILD, defined by
        /// KillerShot.csproj when it publishes self-contained).</summary>
        internal static bool IsStoreBuild { get; set; } =
#if STORE_BUILD
            true;
#else
            false;
#endif

        static partial void ChooseUpdateTarget(UpdateTarget target)
        {
            if (IsStoreBuild) target.Unsupported = true;
        }
    }
}
