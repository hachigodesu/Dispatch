using System.Globalization;
using System.Text.RegularExpressions;
using Dispatch.Core.Sii;
using Dispatch.Core.Sii.Text;

namespace Dispatch.Core.Profiles;

/// <summary>The six skills, 0–6 each; ADR counts the cargo classes unlocked (the save keeps them as a bit mask).</summary>
public sealed record StatsSkills(int Adr, int LongDistance, int HighValue, int Fragile, int JustInTime, int EcoDriving);

public sealed record TruckStats(string Model, string Plate, long OdometerKm, double WearPercent, string? GarageCity, string? DriverHometown);

/// <param name="Slots">How many trucks the garage holds.</param>
/// <param name="Revenue">Income in the days the game keeps (see <see cref="ProfileStats.Days"/>).</param>
public sealed record GarageStats(string City, int Slots, int Trucks, int Drivers, double Productivity, long Revenue, long Profit);

public sealed record DriverStats(string Hometown, string CurrentCity, long Experience, int Level, StatsSkills Skills, string? GarageCity);

/// <summary>How a job was taken (the delivery log's market field).</summary>
public enum JobKind
{
    /// <summary>"compn": a job from the cargo market (your own truck and trailer).</summary>
    CargoMarket,

    /// <summary>"quick": a quick job with a provided truck.</summary>
    QuickJob,

    /// <summary>"spec_oversize": a special transport.</summary>
    SpecialTransport,

    /// <summary>Anything else (shown with its raw market name).</summary>
    Other,
}

/// <summary>
/// One job the player finished, from the save's delivery log. Tokens: companies "blt_ru", cities "omsk", cargo
/// "fish_chips", truck "daf.2021". Times are in-game minutes since the profile started.
/// </summary>
public sealed record JobStats(long StartMinute, long EndMinute, string SourceCompany, string SourceCity, string TargetCompany,
                              string TargetCity, string Cargo, double MassKg, int Units, int PlannedKm, int DrivenKm, long Revenue,
                              long OfferedRevenue, double Damage, long Xp, string Truck, JobKind Kind, string Market)
{
    public int Day => (int)(EndMinute / 1440);
}

/// <summary>One in-game day of the company's books (all garages).</summary>
public sealed record DayStats(int Day, long Revenue, long Wages, long Maintenance, long Fuel, long DistanceKm)
{
    public long Profit => Revenue - Wages - Maintenance - Fuel;
}

/// <summary>What a profile's newest save says about the company and the player. City values are tokens ("bari").</summary>
public sealed record ProfileStats
{
    public required string SaveLabel { get; init; }

    public required DateTime SaveWriteUtc { get; init; }

    public string? CompanyName { get; init; }

    public string? HqCity { get; init; }

    public DateTime? CreatedUtc { get; init; }

    public long Money { get; init; }

    public int Loans { get; init; }

    public long LoanTotal { get; init; }

    public long Experience { get; init; }

    public int Level { get; init; }

    /// <summary>Kilometres the player drove (what the game's profile screen shows).</summary>
    public long DistanceKm { get; init; }

    /// <summary>Kilometres on all the company's trucks' odometers.</summary>
    public long FleetKm => Trucks.Sum(t => t.OdometerKm);

    /// <summary>In-game minutes since the profile started.</summary>
    public long GameMinutes { get; init; }

    public int VisitedCities { get; init; }

    public double ExploredRatio { get; init; }

    public int UnlockedDealers { get; init; }

    public int UnlockedRecruitments { get; init; }

    public required StatsSkills Skills { get; init; }

    public int Fines { get; init; }

    public long FinesTotal { get; init; }

    public int FerryTrips { get; init; }

    public int Trailers { get; init; }

    public required IReadOnlyList<TruckStats> Trucks { get; init; }

    public required IReadOnlyList<GarageStats> Garages { get; init; }

    public required IReadOnlyList<DriverStats> Drivers { get; init; }

    /// <summary>The company's books per day, oldest first, for as many days as the game keeps (14).</summary>
    public required IReadOnlyList<DayStats> Days { get; init; }

    /// <summary>Every job the player finished (the game's delivery log keeps them all), oldest first.</summary>
    public IReadOnlyList<JobStats> Jobs { get; init; } = [];

    public long Revenue => Days.Sum(d => d.Revenue);

    public long Profit => Days.Sum(d => d.Profit);
}

/// <summary>Experience needed per level, from the game's <c>def/economy_data_xp.sii</c> (<c>level_xp[n]</c>).</summary>
public sealed partial class XpLevels(IReadOnlyList<long> perLevel)
{
    public IReadOnlyList<long> PerLevel { get; } = perLevel;

    public static XpLevels Parse(string text) =>
        new(LevelXp().Matches(text).Select(m => (Index: int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                                                 Xp: long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)))
                          .OrderBy(x => x.Index).Select(x => x.Xp).ToList());

    /// <summary>The level for this much experience (0 at the start); past the table the last step repeats.</summary>
    public int Level(long experience)
    {
        if (PerLevel.Count == 0)
        {
            return 0;
        }

        int level = 0;
        long needed = 0;
        while (true)
        {
            needed += PerLevel[Math.Min(level, PerLevel.Count - 1)];
            if (experience < needed || level > 10_000)
            {
                return level;
            }

            level++;
        }
    }

    [GeneratedRegex(@"level_xp\[(\d+)\]\s*:\s*(\d+)")]
    private static partial Regex LevelXp();
}

/// <summary>Reads <see cref="ProfileStats"/> from a profile's newest save (read-only; nothing is written).</summary>
public static partial class ProfileStatsReader
{
    private static readonly string[] WearKeys = ["engine_wear", "transmission_wear", "cabin_wear", "chassis_wear"];

    /// <summary>The newest save's stats, or null when the profile has no save.</summary>
    /// <exception cref="InvalidDataException">The save can't be decoded.</exception>
    public static ProfileStats? Read(ProfileInfo profile, XpLevels? xp)
    {
        if (SaveReader.Newest(profile) is not { } save)
        {
            return null;
        }

        string folder = Path.Combine(profile.MainDir, "save", save.Slot);
        string game = Decode(Path.Combine(folder, "game.sii"));
        string? info = TryDecode(Path.Combine(folder, "info.sii"));
        string? profileText = TryDecode(profile.ProfileSiiPath);
        return Parse(game, info, profileText, xp, save.Label, save.WriteUtc);
    }

    public static ProfileStats Parse(string gameText, string? infoText, string? profileText, XpLevels? xp, string saveLabel, DateTime saveWriteUtc)
    {
        var units = new Dictionary<string, SiiUnit>(StringComparer.Ordinal);
        var byClass = new Dictionary<string, List<SiiUnit>>(StringComparer.Ordinal);
        foreach (var u in SiiUnitReader.Read(gameText))
        {
            units.TryAdd(u.Name, u);
            if (!byClass.TryGetValue(u.ClassName, out var list))
            {
                byClass[u.ClassName] = list = [];
            }

            list.Add(u);
        }

        SiiUnit? First(string cls) => byClass.TryGetValue(cls, out var l) ? l[0] : null;
        SiiUnit? Ref(string? name) => name is null or "null" ? null : units.GetValueOrDefault(name);
        IEnumerable<SiiUnit> Refs(SiiUnit? owner, string key) => owner is null ? [] : owner.GetArray(key).Select(Ref).OfType<SiiUnit>();

        var economy = First("economy");
        var bank = Ref(economy?.Get("bank")) ?? First("bank");
        var player = Ref(economy?.Get("player")) ?? First("player");
        var profileUnit = profileText is null ? null : SiiUnitReader.Read(profileText).FirstOrDefault(u => u.ClassName == "user_profile");
        var infoUnit = infoText is null ? null : SiiUnitReader.Read(infoText).FirstOrDefault(u => u.ClassName == "save_container");

        long experience = Long(economy?.Get("experience_points"));
        var loans = Refs(bank, "loans").ToList();

        // Who drives what: garages list their trucks and drivers slot by slot.
        var garageOfTruck = new Dictionary<string, string>(StringComparer.Ordinal);
        var garageOfDriver = new Dictionary<string, string>(StringComparer.Ordinal);
        var driverOfTruck = new Dictionary<string, string>(StringComparer.Ordinal);
        var garages = new List<GarageStats>();
        var days = new SortedDictionary<int, (long Revenue, long Wages, long Maintenance, long Fuel, long Km)>();
        foreach (var g in byClass.GetValueOrDefault("garage") ?? [])
        {
            if (Long(g.Get("status")) == 0)
            {
                continue; // not owned
            }

            string city = g.Name.StartsWith("garage.", StringComparison.Ordinal) ? g.Name["garage.".Length..] : g.Name;
            var vehicles = g.GetArray("vehicles");
            var drivers = g.GetArray("drivers");
            for (int i = 0; i < vehicles.Count; i++)
            {
                if (vehicles[i] != "null")
                {
                    garageOfTruck[vehicles[i]] = city;
                    if (i < drivers.Count && drivers[i] != "null")
                    {
                        driverOfTruck[vehicles[i]] = drivers[i];
                    }
                }
            }

            foreach (string d in drivers.Where(d => d != "null"))
            {
                garageOfDriver[d] = city;
            }

            long revenue = 0, profit = 0;
            foreach (var e in Refs(Ref(g.Get("profit_log")), "stats_data"))
            {
                long r = Long(e.Get("revenue")), w = Long(e.Get("wage")), m = Long(e.Get("maintenance")), f = Long(e.Get("fuel"));
                revenue += r;
                profit += r - w - m - f;
                int day = (int)Long(e.Get("timestamp_day"));
                var cur = days.GetValueOrDefault(day);
                days[day] = (cur.Revenue + r, cur.Wages + w, cur.Maintenance + m, cur.Fuel + f, cur.Km + Long(e.Get("distance")));
            }

            garages.Add(new GarageStats(city, vehicles.Count, vehicles.Count(v => v != "null"), drivers.Count(d => d != "null"),
                                        Num(g.Get("productivity")), revenue, profit));
        }

        // The player's own truck(s) and every hired driver's.
        var trucks = new List<TruckStats>();
        foreach (string truckName in (player?.GetArray("trucks") ?? []).Where(t => t != "null").Distinct())
        {
            if (Ref(truckName) is not { } v)
            {
                continue;
            }

            string model = Refs(v, "accessories").Select(a => a.Get("data_path")).OfType<string>()
                               .Select(p => TruckPath().Match(p)).Where(m => m.Success).Select(m => m.Groups[1].Value).FirstOrDefault() ?? "";
            // "AB 123<offset hshift=-1>CD|italy": the plate's text without the game's layout tags or country.
            string plate = Whitespace().Replace(Markup().Replace((v.Get("license_plate") ?? "").Split('|')[0], ""), " ").Trim();
            double wear = WearKeys.Max(k => Num(v.Get(k)));
            string? driverHome = driverOfTruck.TryGetValue(truckName, out var dn) ? Ref(dn)?.Get("hometown") : null;
            trucks.Add(new TruckStats(model, plate, Long(v.Get("odometer")), wear * 100, garageOfTruck.GetValueOrDefault(truckName), driverHome));
        }

        var driverStats = new List<DriverStats>();
        foreach (string driverName in (player?.GetArray("drivers") ?? []).Where(d => d != "null").Distinct())
        {
            if (Ref(driverName) is not { ClassName: "driver_ai" } d)
            {
                continue;
            }

            long dxp = Long(d.Get("experience_points"));
            driverStats.Add(new DriverStats(d.Get("hometown") ?? "", d.Get("current_city") ?? "", dxp, xp?.Level(dxp) ?? 0, Skills(d),
                                            garageOfDriver.GetValueOrDefault(driverName)));
        }

        var police = First("police_offence_log");
        long created = Long(profileUnit?.Get("creation_time"));
        return new ProfileStats
        {
            SaveLabel = saveLabel,
            SaveWriteUtc = saveWriteUtc,
            CompanyName = profileUnit?.Get("company_name"),
            HqCity = player?.Get("hq_city"),
            CreatedUtc = created > 0 ? DateTimeOffset.FromUnixTimeSeconds(created).UtcDateTime : null,
            Money = Long(bank?.Get("money_account")),
            Loans = loans.Count,
            LoanTotal = loans.Sum(l => Long(l.Get("amount"))),
            Experience = experience,
            Level = xp?.Level(experience) ?? 0,
            DistanceKm = Long(profileUnit?.Get("cached_distance")),
            GameMinutes = Long(economy?.Get("game_time")),
            VisitedCities = (int)Long(infoUnit?.Get("info_visited_cities")),
            ExploredRatio = Num(infoUnit?.Get("info_explored_ratio")),
            UnlockedDealers = (int)Long(infoUnit?.Get("info_unlocked_dealers")),
            UnlockedRecruitments = (int)Long(infoUnit?.Get("info_unlocked_recruitments")),
            Skills = economy is null ? new StatsSkills(0, 0, 0, 0, 0, 0) : Skills(economy),
            Fines = (int)(police?.GetArray("offence_total_counts").Sum(Long) ?? 0),
            FinesTotal = police?.GetArray("offence_total_fines").Sum(Long) ?? 0,
            FerryTrips = First("ferry_log")?.GetArray("entries").Count ?? 0,
            Trailers = player?.GetArray("trailers").Count(t => t != "null") ?? 0,
            Trucks = trucks,
            Garages = garages.OrderByDescending(g => g.Slots).ThenBy(g => g.City, StringComparer.Ordinal).ToList(),
            Drivers = driverStats.OrderByDescending(d => d.Experience).ToList(),
            Days = days.Select(kv => new DayStats(kv.Key, kv.Value.Revenue, kv.Value.Wages, kv.Value.Maintenance, kv.Value.Fuel, kv.Value.Km)).ToList(),
            Jobs = Refs(Ref(economy?.Get("delivery_log")) ?? First("delivery_log"), "entries").Select(Job).OfType<JobStats>().ToList(),
        };
    }

    /// <summary>
    /// A delivery log entry's <c>params[]</c>, matched against jobs in the profit logs (spike notes in PLAN.md):
    /// 0 end minute, 1/2 "company.volatile.&lt;company&gt;.&lt;city&gt;", 3 "cargo.&lt;cargo&gt;", 4 XP, 5 payment,
    /// 6 planned km, 7 damage (0–1), 13 offered payment, 15 start minute, 16 "vehicle.&lt;truck&gt;", 17 driven km,
    /// 18 market, 22 mass (kg), 23 units. "freerm" entries aren't jobs (free roaming) and are skipped.
    /// </summary>
    private static JobStats? Job(SiiUnit entry)
    {
        var p = entry.GetArray("params");
        if (p.Count < 24 || p[18] == "freerm")
        {
            return null;
        }

        static (string Company, string City) Place(string value)
        {
            var parts = value.StartsWith("company.volatile.", StringComparison.Ordinal) ? value["company.volatile.".Length..].Split('.') : [];
            return parts.Length == 2 ? (parts[0], parts[1]) : ("", "");
        }

        static string After(string value, string prefix) => value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..] : value;

        var (fromCompany, fromCity) = Place(p[1]);
        var (toCompany, toCity) = Place(p[2]);
        var kind = p[18] switch
        {
            "compn" => JobKind.CargoMarket,
            "quick" => JobKind.QuickJob,
            "spec_oversize" => JobKind.SpecialTransport,
            _ => JobKind.Other,
        };
        return new JobStats(Long(p[15]), Long(p[0]), fromCompany, fromCity, toCompany, toCity, After(p[3], "cargo."), Num(p[22]),
                            (int)Long(p[23]), (int)Long(p[6]), (int)Long(p[17]), Long(p[5]), Long(p[13]), Num(p[7]), Long(p[4]),
                            After(p[16], "vehicle."), kind, p[18]);
    }

    private static StatsSkills Skills(SiiUnit u) => new(
        System.Numerics.BitOperations.PopCount((uint)Long(u.Get("adr"))), (int)Long(u.Get("long_dist")), (int)Long(u.Get("heavy")),
        (int)Long(u.Get("fragile")), (int)Long(u.Get("urgent")), (int)Long(u.Get("mechanical")));

    /// <summary>"123" → 123; "&amp;3f800000" (an IEEE float written as hex) and "1.5" are rounded; anything else is 0.</summary>
    internal static long Long(string? value) => (long)Math.Round(Num(value));

    internal static double Num(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        if (value[0] == '&' && uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint bits))
        {
            float f = BitConverter.UInt32BitsToSingle(bits);
            return float.IsFinite(f) ? f : 0;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d) ? d : 0;
    }

    private static string Decode(string file) => SiiFileCodec.Decode(ReadShared(file)).Text;

    private static string? TryDecode(string file)
    {
        try
        {
            return File.Exists(file) ? Decode(file) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Read while letting the game write (a save in progress then fails to decode and is reported).</summary>
    private static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[fs.Length];
        fs.ReadExactly(buffer);
        return buffer;
    }

    [GeneratedRegex(@"^/def/vehicle/truck/([^/]+)/data\.sii$")]
    private static partial Regex TruckPath();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Markup();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
