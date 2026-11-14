using Dispatch.Core.Profiles;

namespace Dispatch.Core.Tests.Profiles;

public sealed class ProfileStatsTests
{
    // A tiny save in the game's text layout: one owned garage with two slots (one truck + driver), one unowned garage,
    // the player's money, skills, fines and a two-day profit log.
    private const string Game = """
        SiiNunit
        {
        economy : _nameless.1 {
         bank: _nameless.2
         player: _nameless.3
         delivery_log: _nameless.50
         game_time: 4320
         experience_points: 1500
         adr: 5
         long_dist: 2
         heavy: 1
         fragile: 0
         urgent: 3
         mechanical: 6
        }
        bank : _nameless.2 {
         money_account: 123456
         loans: 1
         loans[0]: _nameless.20
        }
        bank_loan : _nameless.20 {
         amount: 50000
        }
        player : _nameless.3 {
         hq_city: bari
         trucks: 2
         trucks[0]: _nameless.4
         trucks[1]: null
         drivers: 1
         drivers[0]: driver.7
         trailers: 2
         trailers[0]: _nameless.30
         trailers[1]: null
        }
        vehicle : _nameless.4 {
         engine_wear: &3e4ccccd
         cabin_wear: 0
         odometer: 158378
         license_plate: "<color value=FF000000>AB <offset hshift=-1 vshift=0>123CD   |italy"
         accessories: 2
         accessories[0]: _nameless.5
         accessories[1]: _nameless.6
        }
        vehicle_accessory : _nameless.5 {
         data_path: "/def/vehicle/truck/iveco.sway/engine/xc13.sii"
        }
        vehicle_accessory : _nameless.6 {
         data_path: "/def/vehicle/truck/iveco.sway/data.sii"
        }
        driver_ai : driver.7 {
         adr: 63
         long_dist: 6
         heavy: 6
         fragile: 6
         urgent: 6
         mechanical: 6
         hometown: torino
         current_city: milano
         experience_points: 700
        }
        garage : garage.bari {
         vehicles: 2
         vehicles[0]: _nameless.4
         vehicles[1]: null
         drivers: 2
         drivers[0]: driver.7
         drivers[1]: null
         status: 2
         profit_log: _nameless.10
         productivity: &3f000000
        }
        garage : garage.roma {
         vehicles: 0
         drivers: 0
         status: 0
        }
        profit_log : _nameless.10 {
         stats_data: 2
         stats_data[0]: _nameless.11
         stats_data[1]: _nameless.12
        }
        profit_log_entry : _nameless.11 {
         revenue: 1000
         wage: 300
         maintenance: 50
         fuel: 150
         distance: 400
         timestamp_day: 3
        }
        profit_log_entry : _nameless.12 {
         revenue: 2000
         wage: 600
         maintenance: 100
         fuel: 300
         distance: 800
         timestamp_day: 2
        }
        police_offence_log : _nameless.40 {
         offence_total_counts: 2
         offence_total_counts[0]: 1
         offence_total_counts[1]: 2
         offence_total_fines: 2
         offence_total_fines[0]: 100
         offence_total_fines[1]: 250
        }
        ferry_log : _nameless.41 {
         entries: 2
         entries[0]: _nameless.42
         entries[1]: _nameless.43
        }
        delivery_log : _nameless.50 {
         version: 1
         entries: 2
         entries[0]: _nameless.51
         entries[1]: _nameless.52
         cached_jobs_count: 1
        }
        delivery_log_entry : _nameless.51 {
         params: 24
         params[0]: "361245"
         params[1]: "company.volatile.blt_ru.ekat"
         params[2]: "company.volatile.peksum.omsk"
         params[3]: "cargo.fish_chips"
         params[4]: "1192"
         params[5]: "30000"
         params[6]: "910"
         params[7]: "0.029"
         params[8]: "420"
         params[9]: "0"
         params[10]: "0"
         params[11]: "2"
         params[12]: "0"
         params[13]: "31277"
         params[14]: "0"
         params[15]: "359906"
         params[16]: "vehicle.daf.2021"
         params[17]: "907"
         params[18]: "compn"
         params[19]: ""
         params[20]: ""
         params[21]: "0"
         params[22]: "17160.000"
         params[23]: "33"
        }
        delivery_log_entry : _nameless.52 {
         params: 24
         params[0]: "4506"
         params[1]: ""
         params[2]: ""
         params[3]: ""
         params[4]: "50"
         params[5]: "0"
         params[6]: "100"
         params[7]: "0.100"
         params[8]: "0"
         params[9]: "0"
         params[10]: "0"
         params[11]: "1"
         params[12]: "0"
         params[13]: "0"
         params[14]: "0"
         params[15]: "0"
         params[16]: ""
         params[17]: "0"
         params[18]: "freerm"
         params[19]: ""
         params[20]: ""
         params[21]: "0"
         params[22]: "-1.000"
         params[23]: "0"
        }
        }
        """;

    private const string Info = """
        SiiNunit
        {
        save_container : _nameless.1 {
         info_visited_cities: 42
         info_unlocked_dealers: 3
         info_unlocked_recruitments: 4
         info_explored_ratio: &3e800000
        }
        }
        """;

    private const string Profile = """
        SiiNunit
        {
        user_profile : _nameless.1 {
         company_name: "Test Haulage"
         cached_distance: 9876
         creation_time: 1700000000
        }
        }
        """;

    [Fact]
    public void Reads_company_fleet_and_books()
    {
        var xp = new XpLevels([200, 500, 700]);
        var s = ProfileStatsReader.Parse(Game, Info, Profile, xp, "autosave", new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc));

        s.CompanyName.ShouldBe("Test Haulage");
        s.HqCity.ShouldBe("bari");
        s.CreatedUtc.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1700000000).UtcDateTime);
        s.Money.ShouldBe(123456);
        s.Loans.ShouldBe(1);
        s.LoanTotal.ShouldBe(50000);
        s.Experience.ShouldBe(1500);
        s.Level.ShouldBe(3);      // 200 + 500 + 700 = 1400 ≤ 1500 < 2100
        s.GameMinutes.ShouldBe(4320);
        s.DistanceKm.ShouldBe(9876);
        s.VisitedCities.ShouldBe(42);
        s.ExploredRatio.ShouldBe(0.25, 1e-6);
        s.UnlockedDealers.ShouldBe(3);
        s.UnlockedRecruitments.ShouldBe(4);
        s.Skills.ShouldBe(new StatsSkills(2, 2, 1, 0, 3, 6)); // adr 5 = two classes
        s.Fines.ShouldBe(3);
        s.FinesTotal.ShouldBe(350);
        s.FerryTrips.ShouldBe(2);
        s.Trailers.ShouldBe(1);

        var truck = s.Trucks.ShouldHaveSingleItem();
        truck.ShouldBe(new TruckStats("iveco.sway", "AB 123CD", 158378, truck.WearPercent, "bari", "torino"));
        truck.WearPercent.ShouldBe(20, 0.01);
        s.FleetKm.ShouldBe(158378);

        var garage = s.Garages.ShouldHaveSingleItem();
        garage.ShouldBe(new GarageStats("bari", 2, 1, 1, 0.5, 3000, 1500));

        var driver = s.Drivers.ShouldHaveSingleItem();
        driver.ShouldBe(new DriverStats("torino", "milano", 700, 2, new StatsSkills(6, 6, 6, 6, 6, 6), "bari"));

        var job = s.Jobs.ShouldHaveSingleItem(); // the free-roam entry isn't a job
        job.ShouldBe(new JobStats(359906, 361245, "blt_ru", "ekat", "peksum", "omsk", "fish_chips", 17160, 33, 910, 907, 30000, 31277,
                                  0.029, 1192, "daf.2021", JobKind.CargoMarket, "compn"));
        job.Day.ShouldBe(250);

        s.Days.Select(d => d.Day).ShouldBe([2, 3]); // oldest first
        s.Days[0].Profit.ShouldBe(1000);
        s.Revenue.ShouldBe(3000);
        s.Profit.ShouldBe(1500);
    }

    [Fact]
    public void Missing_parts_give_zeros_not_errors()
    {
        var s = ProfileStatsReader.Parse("SiiNunit\n{\n}\n", null, null, null, "1", DateTime.UtcNow);
        s.Money.ShouldBe(0);
        s.Level.ShouldBe(0);
        s.Trucks.ShouldBeEmpty();
        s.Days.ShouldBeEmpty();
        s.CompanyName.ShouldBeNull();
    }

    [Fact]
    public void Xp_levels_from_the_game_file_repeat_the_last_step()
    {
        var xp = XpLevels.Parse("xp_level_data : economy.data.xp_levels {\n level_xp[0]: 200\n level_xp[1]: 500\n\tlevel_xp[2]: 700\t\n}");
        xp.PerLevel.ShouldBe([200L, 500L, 700L]);
        xp.Level(0).ShouldBe(0);
        xp.Level(199).ShouldBe(0);
        xp.Level(200).ShouldBe(1);
        xp.Level(1400).ShouldBe(3);
        xp.Level(1400 + 700 + 700).ShouldBe(5); // past the table: 700 per level
        new XpLevels([]).Level(99999).ShouldBe(0);
    }

    [Fact]
    public void Cargo_and_company_names_in_the_chosen_language_mods_win()
    {
        using var tmp = new Dispatch.Core.Tests.Mods.TempDir();
        string game = tmp.File("def.scs"), locale = tmp.File("locale.scs"), mod = tmp.File("mod.scs");
        static byte[] Utf8(string s) => System.Text.Encoding.UTF8.GetBytes(s);
        Dispatch.Core.Tests.Mods.Fixtures.WriteZip(game, new()
        {
            ["def/cargo/apples_c.sui"] = Utf8("cargo_data: cargo.apples_c\n{\n\tname: \"@@cn_apples@@\"\n\tfragility: 0.4\n}\n"),
            ["def/cargo/fish_chips.sui"] = Utf8("cargo_data: cargo.fish_chips\n{\n\tname: \"@@cn_fish_chips@@\"\n}\n"),
            ["def/company/blt_ru.sui"] = Utf8("company_permanent: company.permanent.blt_ru\n{\n\tname: \"БЛТ\"\n\tsort_name: \"blt\"\n}\n"),
        });
        Dispatch.Core.Tests.Mods.Fixtures.WriteZip(locale, new()
        {
            ["locale/en_us/localization.sui"] = Utf8("\tkey[]: \"cn_apples\"\n\tval[]: \"Apples\"\n\n\tkey[]: \"cn_fish_chips\"\n\tval[]: \"Fish Fingers\"\n"),
            ["locale/de_de/localization.sui"] = Utf8("\tkey[]: \"cn_apples\"\n\tval[]: \"Äpfel\"\n"),
        });
        Dispatch.Core.Tests.Mods.Fixtures.WriteZip(mod, new()
        {
            ["def/cargo/apples_c.sui"] = Utf8("cargo_data: cargo.apples_c\n{\n\tname: \"Mod Apples\"\n}\n"),
        });

        var en = GameNames.Read([game, locale], GameNames.LocaleFolder("en-GB"), TestContext.Current.CancellationToken);
        en.Cargo("apples_c").ShouldBe("Apples");
        en.Cargo("fish_chips").ShouldBe("Fish Fingers");
        en.Company("blt_ru").ShouldBe("БЛТ");
        en.Cargo("unknown").ShouldBeNull();

        var de = GameNames.Read([game, locale], GameNames.LocaleFolder("de"), TestContext.Current.CancellationToken);
        de.Cargo("apples_c").ShouldBe("Äpfel");
        de.Cargo("fish_chips").ShouldBe("cn_fish_chips"); // no German translation: the key, not "@@…@@"

        GameNames.Read([game, locale, mod], "en_us", TestContext.Current.CancellationToken).Cargo("apples_c").ShouldBe("Mod Apples");
        GameNames.LocaleFolder("pt-BR").ShouldBe("pt_br");
        GameNames.LocaleFolder("ja").ShouldBe("en_us");
    }

    [Theory]
    [InlineData("&3f800000", 1.0)]
    [InlineData("&bf000000", -0.5)]
    [InlineData("42", 42.0)]
    [InlineData("1.25", 1.25)]
    [InlineData("nil", 0.0)]
    [InlineData(null, 0.0)]
    public void Numbers_in_the_save_format(string? text, double expected) => ProfileStatsReader.Num(text).ShouldBe(expected, 1e-6);
}
