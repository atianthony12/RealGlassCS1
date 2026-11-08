namespace RealGlass.Glass
{
    /// <summary>
    /// The kinds of building the glass can be switched off on (Settings.GlassOn*; the author's call,
    /// 2026-11-08), by what the game says a building is: its ItemClass service, and for the Monument
    /// service its editor category -- the game files its wonders under "MonumentLandmarks" and every
    /// other unique building under MonumentCategory1..6, Football, Concerts, the expansions' and the
    /// modder packs' (the strings in Assembly-CSharp, read 2026-11-08). A custom asset's category is
    /// the one its creator picked.
    /// </summary>
    public static class GlassKinds
    {
        public const int Residential = 1 << 0;
        public const int Commercial = 1 << 1;
        public const int Offices = 1 << 2;
        public const int Industrial = 1 << 3;
        public const int Services = 1 << 4;
        public const int Transport = 1 << 5;
        public const int Unique = 1 << 6;
        public const int Monuments = 1 << 7;
        public const int Parks = 1 << 8;
        public const int Other = 1 << 9;

        public static readonly string[] Names =
        {
            "residential", "commercial", "offices", "industrial", "services", "transport", "unique", "monuments", "parks", "other",
        };

        /// <summary>The game's category of its wonders (the Monuments tab of the unique buildings).</summary>
        public const string LandmarksCategory = "MonumentLandmarks";

        /// <summary>The kind (one bit) of a building asset; a sub-building's is its main building's (the caller's).</summary>
        public static int Of(BuildingInfo info)
        {
            ItemClass c = info != null ? info.m_class : null;
            if (c == null)
                return Other;

            switch (c.m_service)
            {
                case ItemClass.Service.Residential:
                    return Residential;
                case ItemClass.Service.Commercial:
                case ItemClass.Service.Hotel:
                    return Commercial;
                case ItemClass.Service.Office:
                    return Offices;
                case ItemClass.Service.Industrial:
                case ItemClass.Service.PlayerIndustry:
                case ItemClass.Service.Fishing:
                    return Industrial;
                case ItemClass.Service.HealthCare:
                case ItemClass.Service.Education:
                case ItemClass.Service.PlayerEducation:
                case ItemClass.Service.Museums:
                case ItemClass.Service.VarsitySports:
                case ItemClass.Service.PoliceDepartment:
                case ItemClass.Service.FireDepartment:
                case ItemClass.Service.Disaster:
                case ItemClass.Service.Garbage:
                case ItemClass.Service.Water:
                case ItemClass.Service.Electricity:
                case ItemClass.Service.ServicePoint:
                    return Services;
                case ItemClass.Service.PublicTransport:
                    return Transport;
                case ItemClass.Service.Monument:
                    return info.category == LandmarksCategory ? Monuments : Unique;
                case ItemClass.Service.Beautification:
                case ItemClass.Service.Tourism:
                    return Parks;
                default:
                    return Other;
            }
        }

        /// <summary>The kinds switched off now, from the settings (0: every kind on).</summary>
        public static int OffNow()
        {
            int off = 0;
            off |= Off(Settings.GlassOnResidential, Residential);
            off |= Off(Settings.GlassOnCommercial, Commercial);
            off |= Off(Settings.GlassOnOffices, Offices);
            off |= Off(Settings.GlassOnIndustrial, Industrial);
            off |= Off(Settings.GlassOnServices, Services);
            off |= Off(Settings.GlassOnTransport, Transport);
            off |= Off(Settings.GlassOnUnique, Unique);
            off |= Off(Settings.GlassOnMonuments, Monuments);
            off |= Off(Settings.GlassOnParks, Parks);
            off |= Off(Settings.GlassOnOther, Other);
            return off;
        }

        private static int Off(BoolSetting setting, int bit)
        {
            return setting != null && !setting.value ? bit : 0;
        }

        /// <summary>For the log: "offices, monuments", or "none".</summary>
        public static string Describe(int kinds)
        {
            if (kinds == 0)
                return "none";
            var parts = new System.Collections.Generic.List<string>();
            for (int i = 0; i < Names.Length; i++)
            {
                if ((kinds & (1 << i)) != 0)
                    parts.Add(Names[i]);
            }
            return string.Join(", ", parts.ToArray());
        }
    }
}
