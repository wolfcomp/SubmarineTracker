using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Logging;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.Game.Housing;
using Lumina.Excel;
using Lumina.Excel.GeneratedSheets;
using Newtonsoft.Json;

namespace SubmarineTracker.Data;

public static class Submarines
{
    private static ExcelSheet<Item> ItemSheet = null!;
    private static ExcelSheet<SubmarineRank> RankSheet = null!;
    private static ExcelSheet<SubmarinePart> PartSheet = null!;
    private static ExcelSheet<SubmarineExploration> ExplorationSheet = null!;

    private static List<SubmarineExploration> PossiblePoints = new();

    private const int FixedVoyageTime = 43200; // 12h

    public static void Initialize()
    {
        ItemSheet = Plugin.Data.GetExcelSheet<Item>()!;
        RankSheet = Plugin.Data.GetExcelSheet<SubmarineRank>()!;
        PartSheet = Plugin.Data.GetExcelSheet<SubmarinePart>()!;
        ExplorationSheet = Plugin.Data.GetExcelSheet<SubmarineExploration>()!;

        PossiblePoints = ExplorationSheet.Where(r => r.ExpReward > 0).ToList();
    }

    public class FcSubmarines
    {
        public string CharacterName = "";
        public string Tag = null!;
        public string World = null!;
        public List<Submarine> Submarines = null!;

        public Dictionary<uint, SubmarineLoot> SubLoot = new();
        public Dictionary<uint, bool> UnlockedSectors = new();
        public Dictionary<uint, bool> ExploredSectors = new();

        [JsonConstructor]
        public FcSubmarines() { }

        public FcSubmarines(string characterName, string tag, string world, List<Submarine> submarines, Dictionary<uint, SubmarineLoot> loot, List<Tuple<uint, bool, bool>> points)
        {
            CharacterName = characterName;
            Tag = tag;
            World = world;
            Submarines = submarines;
            SubLoot = loot;
            foreach (var (point, unlocked, explored) in points)
            {
                UnlockedSectors[point] = unlocked;
                ExploredSectors[point] = explored;
            }
        }

        public static FcSubmarines Empty => new("", "", "Unknown", new List<Submarine>(), new Dictionary<uint, SubmarineLoot>(), new List<Tuple<uint, bool, bool>>());

        public void AddSubLoot(uint key, uint returnTime, Span<HousingWorkshopSubmarineGathered> data)
        {
            SubLoot.TryAdd(key, new SubmarineLoot());

            var sub = SubLoot[key];
            sub.Add(returnTime, data);
        }

        public void GetUnlockedAndExploredSectors()
        {
            foreach (var submarineExploration in ExplorationSheet)
            {
                UnlockedSectors[submarineExploration.RowId] = HousingManager.IsSubmarineExplorationUnlocked((byte) submarineExploration.RowId);
                ExploredSectors[submarineExploration.RowId] = HousingManager.IsSubmarineExplorationExplored((byte) submarineExploration.RowId);
            }
        }

        #region Loot
        [JsonIgnore] public bool Refresh = true;
        [JsonIgnore] public Dictionary<uint, Dictionary<Item, int>> AllLoot = new();

        public void RebuildStats()
        {
            if (Refresh)
                Refresh = false;
            else
                return;


            AllLoot.Clear();
            foreach (var point in PossiblePoints)
            {
                var possibleLoot = SubLoot.Values.SelectMany(val => val.LootForPoint(point.RowId)).ToList();
                foreach (var pointLoot in possibleLoot)
                {
                    var lootList = AllLoot.GetOrCreate(point.RowId);
                    if (!lootList.TryAdd(pointLoot.PrimaryItem, pointLoot.PrimaryCount))
                        lootList[pointLoot.PrimaryItem] += pointLoot.PrimaryCount;

                    if (!pointLoot.ValidAdditional)
                        continue;

                    if (!lootList.TryAdd(pointLoot.AdditionalItem, pointLoot.AdditionalCount))
                        lootList[pointLoot.AdditionalItem] += pointLoot.AdditionalCount;
                }
            }
        }

        #endregion
    }

    public class SubmarineLoot
    {
        public Dictionary<uint, List<DetailedLoot>> Loot = new();

        [JsonConstructor]
        public SubmarineLoot() {}

        public void Add(uint returnTime, Span<HousingWorkshopSubmarineGathered> data)
        {
            if (data[0].ItemIdPrimary == 0)
                return;

            if (!Loot.TryAdd(returnTime, new List<DetailedLoot>()))
                return;

            foreach (var val in data.ToArray().Where(val => val.Point > 0))
                Loot[returnTime].Add(new DetailedLoot(val));
        }

        public IEnumerable<DetailedLoot> LootForPoint(uint point)
        {
            return Loot.Values.SelectMany(val => val.Where(iVal => iVal.Point == point));
        }
    }

    public record DetailedLoot(uint Point, uint Primary, ushort PrimaryCount, bool PrimaryHQ, uint Additional, ushort AdditionalCount, bool AdditionalHQ)
    {
        [JsonConstructor]
        public DetailedLoot() : this(0, 0, 0, false, 0, 0, false) { }

        public DetailedLoot(HousingWorkshopSubmarineGathered data) : this(0,0,0, false, 0, 0, false)
        {
            Point = data.Point;
            Primary = data.ItemIdPrimary;
            PrimaryCount = data.ItemCountPrimary;
            PrimaryHQ = data.ItemHQPrimary;

            Additional = data.ItemIdAdditional;
            AdditionalCount = data.ItemCountAdditional;
            AdditionalHQ = data.ItemHQAdditional;
        }

        [JsonIgnore] public Item PrimaryItem => ItemSheet.GetRow(Primary)!;
        [JsonIgnore] public Item AdditionalItem => ItemSheet.GetRow(Additional)!;
        [JsonIgnore] public bool ValidAdditional => Additional > 0;
    }

    public record Submarine(string Name, ushort Rank, ushort Hull, ushort Stern, ushort Bow, ushort Bridge, uint CExp, uint NExp)
    {
        public uint Register;
        public uint Return;
        public DateTime ReturnTime;
        public readonly List<uint> Points = new();

        [JsonConstructor]
        public Submarine() : this("", 0, 0,0,0,0, 0, 0) { }

        public unsafe Submarine(HousingWorkshopSubmersibleSubData data) : this("", 0, 0,0,0,0, 0, 0)
        {
            Name = MemoryHelper.ReadSeStringNullTerminated(new nint(data.Name)).ToString();
            Rank = data.RankId;
            Hull = data.HullId;
            Stern = data.SternId;
            Bow = data.BowId;
            Bridge = data.BridgeId;
            CExp = data.CurrentExp;
            NExp = data.NextLevelExp;

            Register = data.RegisterTime;
            Return = data.ReturnTime;
            ReturnTime = data.GetReturnTime();

            var managedArray = new byte[5];
            Marshal.Copy((nint) data.CurrentExplorationPoints, managedArray, 0, 5);

            foreach (var point in managedArray)
            {
                if (point > 0)
                    Points.Add(point);
            }
        }

        private string GetPartName(ushort partId) => ItemSheet.GetRow(PartIdToItemId[partId])!.Name.ToString();
        private uint GetIconId(ushort partId) => ItemSheet.GetRow(PartIdToItemId[partId])!.Icon;

        #region parts
        [JsonIgnore] public string HullName   => GetPartName(Hull);
        [JsonIgnore] public string SternName  => GetPartName(Stern);
        [JsonIgnore] public string BowName    => GetPartName(Bow);
        [JsonIgnore] public string BridgeName => GetPartName(Bridge);

        [JsonIgnore] public uint HullIconId   => GetIconId(Hull);
        [JsonIgnore] public uint SternIconId  => GetIconId(Stern);
        [JsonIgnore] public uint BowIconId    => GetIconId(Bow);
        [JsonIgnore] public uint BridgeIconId => GetIconId(Bridge);

        [JsonIgnore] public SubmarineBuild Build => new SubmarineBuild(this);

        public string BuildIdentifier()
        {
            var identifier = $"{ToIdentifier(Hull)}{ToIdentifier(Stern)}{ToIdentifier(Bow)}{ToIdentifier(Bridge)}";

            if (identifier.Count(l => l == '+') == 4)
                identifier = $"{identifier.Replace("+", "")}++";

            return identifier;
        }
        #endregion

        public bool IsValid() => Rank > 0;
        public bool ValidExpRange() => NExp > 0;
        public bool IsOnVoyage() => Points.Any();

        #region equals
        public bool VoyageEqual(List<uint> l, List<uint> r) => l.SequenceEqual(r);

        public virtual bool Equals(Submarine? other)
        {
            if (ReferenceEquals(null, other))
                return false;
            if (ReferenceEquals(this, other))
                return true;
            return Name == other.Name && Rank == other.Rank && Hull == other.Hull &&
                   Stern == other.Stern && Bow == other.Bow && Bridge == other.Bridge &&
                   CExp == other.CExp && Return == other.Return && Register == other.Register &&
                   ReturnTime == other.ReturnTime && VoyageEqual(Points, other.Points);
        }

        public bool Equals(Submarine x, Submarine y)
        {
            if (ReferenceEquals(x, y))
                return true;
            if (ReferenceEquals(x, null))
                return false;
            if (ReferenceEquals(y, null))
                return false;
            if (x.GetType() != y.GetType())
                return false;

            return x.Name == y.Name && x.Rank == y.Rank && x.Hull == y.Hull &&
                   x.Stern == y.Stern && x.Bow == y.Bow && x.Bridge == y.Bridge &&
                   x.CExp == y.CExp && x.Return == y.Return && x.Register == y.Register &&
                   x.ReturnTime == y.ReturnTime && VoyageEqual(x.Points, y.Points);
        }

        public override int GetHashCode() => HashCode.Combine(Name, Rank, Hull, Stern, Bow, Bridge, CExp, Points);
        #endregion
    }

    public readonly struct SubmarineBuild
    {
        private readonly SubmarineRank Bonus;
        private readonly SubmarinePart Hull;
        private readonly SubmarinePart Stern;
        private readonly SubmarinePart Bow;
        private readonly SubmarinePart Bridge;

        public SubmarineBuild(Submarine sub) : this(sub.Rank, sub.Hull, sub.Stern, sub.Bow, sub.Bridge) { }

        public SubmarineBuild(int rank, int hull, int stern, int bow, int bridge)
        {
            Bonus = GetRank(rank);
            Hull = GetPart(hull);
            Stern = GetPart(stern);
            Bow = GetPart(bow);
            Bridge = GetPart(bridge);
        }

        public int Surveillance => Bonus.SurveillanceBonus + Hull.Surveillance + Stern.Surveillance + Bow.Surveillance + Bridge.Surveillance;
        public int Retrieval => Bonus.RetrievalBonus + Hull.Retrieval + Stern.Retrieval + Bow.Retrieval + Bridge.Retrieval;
        public int Speed => Bonus.SpeedBonus + Hull.Speed + Stern.Speed + Bow.Speed + Bridge.Speed;
        public int Range => Bonus.RangeBonus + Hull.Range + Stern.Range + Bow.Range + Bridge.Range;
        public int Favor => Bonus.FavorBonus + Hull.Favor + Stern.Favor + Bow.Favor + Bridge.Favor;
        public int RepairCosts => Hull.RepairMaterials + Stern.RepairMaterials + Bow.RepairMaterials + Bridge.RepairMaterials;

        private SubmarineRank GetRank(int rank) => RankSheet.GetRow((uint) rank)!;
        private SubmarinePart GetPart(int partId) => PartSheet.GetRow((uint) partId)!;

        public bool EqualsSubmarine(Submarine other)
        {
            return Bonus.RowId == other.Rank && Hull.RowId == other.Hull && Stern.RowId == other.Stern && Bow.RowId == other.Bow && Bridge.RowId == other.Bridge;
        }
    }

    public static bool SubmarinesEqual(List<Submarine> l, List<Submarine> r)
    {
        if (!l.Any() || !r.Any())
            return false;

        if (l.Count != r.Count)
            return false;

        foreach (var (subL, subR) in l.Zip(r))
        {
            if (!subL.Equals(subR))
                return false;
        }

        return true;
    }

    public static uint FindVoyageStartPoint(uint point)
    {
        var startPoints = ExplorationSheet.Where(s => s.Passengers).Select(s => s.RowId).ToList();
        startPoints.Reverse();

        // This works because we reversed the list of start points
        foreach (var possibleStart in startPoints)
        {
            if (point > possibleStart)
                return possibleStart;
        }

        return 0;
    }

    public static readonly Dictionary<ulong, FcSubmarines> KnownSubmarines = new();

    public static readonly Dictionary<ushort, uint> PartIdToItemId = new Dictionary<ushort, uint>
    {
        // Shark
        { 1, 21792 }, // Bow
        { 2, 21793 }, // Bridge
        { 3, 21794 }, // Hull
        { 4, 21795 }, // Stern

        // Ubiki
        { 5, 21796 },
        { 6, 21797 },
        { 7, 21798 },
        { 8, 21799 },

        // Whale
        { 9, 22526 },
        { 10, 22527 },
        { 11, 22528 },
        { 12, 22529 },

        // Coelacanth
        { 13, 23903 },
        { 14, 23904 },
        { 15, 23905 },
        { 16, 23906 },

        // Syldra
        { 17, 24344 },
        { 18, 24345 },
        { 19, 24346 },
        { 20, 24347 },

        // Modified same order
        { 21, 24348 },
        { 22, 24349 },
        { 23, 24350 },
        { 24, 24351 },

        { 25, 24352 },
        { 26, 24353 },
        { 27, 24354 },
        { 28, 24355 },

        { 29, 24356 },
        { 30, 24357 },
        { 31, 24358 },
        { 32, 24359 },

        { 33, 24360 },
        { 34, 24361 },
        { 35, 24362 },
        { 36, 24363 },

        { 37, 24364 },
        { 38, 24365 },
        { 39, 24366 },
        { 40, 24367 }
    };

    public static string ToIdentifier(ushort partId)
    {
        return ((partId - 1) / 4) switch
        {
            0 => "S",
            1 => "U",
            2 => "W",
            3 => "C",
            4 => "Y",

            5 => $"{ToIdentifier((ushort)(partId - 20))}+",
            6 => $"{ToIdentifier((ushort)(partId - 20))}+",
            7 => $"{ToIdentifier((ushort)(partId - 20))}+",
            8 => $"{ToIdentifier((ushort)(partId - 20))}+",
            9 => $"{ToIdentifier((ushort)(partId - 20))}+",
            _ => "Unknown"
        };
    }

    #region Character Handler
    public static void LoadCharacters()
    {
        foreach (var file in Plugin.PluginInterface.ConfigDirectory.EnumerateFiles())
        {
            ulong id;
            try
            {
                id = Convert.ToUInt64(Path.GetFileNameWithoutExtension(file.Name));
            }
            catch (Exception e)
            {
                PluginLog.Error($"Found file that isn't convertable. Filename: {file.Name}");
                PluginLog.Error(e.Message);
                continue;
            }

            var config = CharacterConfiguration.Load(id);

            KnownSubmarines.TryAdd(id, FcSubmarines.Empty);
            var playerFc = KnownSubmarines[id];

            if (SubmarinesEqual(playerFc.Submarines, config.Submarines))
                continue;

            KnownSubmarines[id] = new FcSubmarines(config.CharacterName, config.Tag, config.World, config.Submarines, config.Loot, config.ExplorationPoints);
        }
    }

    public static void SaveCharacter()
    {
        var id = Plugin.ClientState.LocalContentId;
        if (!KnownSubmarines.TryGetValue(id, out var playerFc))
            return;

        var points = playerFc.UnlockedSectors.Select(t => new Tuple<uint, bool, bool>(t.Key, t.Value, playerFc.ExploredSectors[t.Key])).ToList();

        var config = new CharacterConfiguration(id, playerFc.CharacterName, playerFc.Tag, playerFc.World, playerFc.Submarines, playerFc.SubLoot, points);
        config.Save();
    }

    public static void DeleteCharacter(ulong id)
    {
        if (!KnownSubmarines.ContainsKey(id))
            return;

        KnownSubmarines.Remove(id);
        var file = Plugin.PluginInterface.ConfigDirectory.EnumerateFiles().FirstOrDefault(f => f.Name == $"{id}.json");
        if (file == null)
            return;

        try
        {
            file.Delete();
        }
        catch (Exception e)
        {
            PluginLog.Error("Error while deleting character save file.");
            PluginLog.Error(e.Message);
        }
    }
    #endregion

    #region Optimizer
    public static uint CalculateDuration(IEnumerable<uint> walkingPoints, SubmarineBuild build)
    {
        // spin it up?
        if (VoyageTime(32, 33, 100) == 0 || SurveyTime(33, 100) == 0)
        {
            PluginLog.Warning("GetSubmarineVoyageTime or SurveyTime was zero.");
            return 0;
        }

        var start = ExplorationSheet.GetRow(walkingPoints.First())!;

        var points = new List<SubmarineExploration>();
        foreach (var p in walkingPoints.Skip(1))
            points.Add(ExplorationSheet.GetRow(p)!);

        switch (points.Count)
        {
            case 0:
                return 0;
            case 1: // 1 point makes no sense to optimize, so just return distance
            {
                var onlyPoint = points[0];
                return VoyageTime(start.RowId, onlyPoint.RowId, (short) build.Speed) + SurveyTime(onlyPoint.RowId, (short) build.Speed) + FixedVoyageTime;
            }
            case > 5: // More than 5 points isn't allowed ingame
                return 0;
        }

        var allDurations = new List<long>();
        for (var i = 0; i < points.Count; i++)
        {
            var voyage = i == 0
                             ? VoyageTime(start.RowId, points[0].RowId, (short)build.Speed)
                             : VoyageTime(points[i - 1].RowId, points[i].RowId, (short)build.Speed);
            var survey = SurveyTime(points[i].RowId, (short)build.Speed);
            allDurations.Add(voyage + survey);
        }

        return (uint)allDurations.Sum() + FixedVoyageTime;
    }

    public static (int Distance, List<uint> Points) CalculateDistance(IEnumerable<uint> walkingPoints)
    {
        // spin it up?
        if (HousingManager.GetSubmarineVoyageDistance(32, 33) == 0)
        {
            PluginLog.Warning("GetSubmarineVoyageDistance was zero.");
            return (0, new List<uint>());
        }

        var start = ExplorationSheet.GetRow(walkingPoints.First())!;

        var points = new List<SubmarineExploration>();
        foreach (var p in walkingPoints.Skip(1))
            points.Add(ExplorationSheet.GetRow(p)!);


        // zero
        if (points.Count == 0)
            return (0, new List<uint>());

        // 1 point makes no sense to optimize, so just return distance
        if (points.Count == 1)
        {
            var onlyPoint = points[0];
            var distance = BestDistance(start.RowId, onlyPoint.RowId) + onlyPoint.SurveyDistance;
            return ((int) distance, new List<uint> { onlyPoint.RowId });
        }

        // More than 5 points isn't allowed ingame
        if (points.Count > 5)
            return (0, new List<uint>());

        List<(uint Key, uint Start, Dictionary<uint, uint> Distances)> AllDis = new();
        foreach (var (point, idx) in points.Select((val, i) => (val, i)))
        {
            AllDis.Add((point.RowId, BestDistance(start.RowId, point.RowId), new()));

            foreach (var iPoint in points)
            {
                if (point.RowId == iPoint.RowId)
                    continue;

                AllDis[idx].Distances.Add(iPoint.RowId, BestDistance(point.RowId, iPoint.RowId));
            }
        }

        List<(uint Way, List<uint> Points)> MinimalWays = new List<(uint Way, List<uint> Points)>();
        try
        {
            foreach (var (point, idx) in AllDis.Select((val, i) => (val, i)))
            {
                var otherPoints = AllDis.ToList();
                otherPoints.RemoveAt(idx);

                var others = new Dictionary<uint, Dictionary<uint, uint>>();
                foreach (var p  in otherPoints)
                {
                    var listDis = new Dictionary<uint, uint>();
                    foreach (var dis in p.Distances)
                    {
                        listDis.Add(dis.Key, dis.Value);
                    }

                    others[p.Key] = listDis;
                }

                MinimalWays.Add(PathWalker(point, others));
            }
        }
        catch (Exception e)
        {
            PluginLog.Error(e.Message);
            PluginLog.Error(e.StackTrace);
        }

        var min = MinimalWays.MinBy(m => m.Way);
        var surveyD = min.Points.Sum(d => ExplorationSheet.GetRow(d)!.SurveyDistance);
        return ((int) min.Way + surveyD, min.Points);
    }

    public static (uint Distance, List<uint> Points) PathWalker((uint Key, uint Start, Dictionary<uint, uint> Distances) point, Dictionary<uint, Dictionary<uint, uint>> otherPoints)
    {
        List<(uint Distance, List<uint> Points)> possibleDistances = new();
        foreach (var pos1 in otherPoints)
        {
            if (point.Key == pos1.Key)
                continue;

            var startToFirst = point.Start + point.Distances[pos1.Key];

            if (otherPoints.Count == 1)
            {
                possibleDistances.Add((startToFirst, new List<uint> { point.Key, pos1.Key, }));
                continue;
            }

            foreach (var pos2 in otherPoints)
            {
                if (pos1.Key == pos2.Key || point.Key == pos2.Key)
                    continue;

                var startToSecond = startToFirst + otherPoints[pos1.Key][pos2.Key];

                if (otherPoints.Count == 2)
                {
                    possibleDistances.Add((startToSecond, new List<uint> { point.Key, pos1.Key, pos2.Key, }));
                    continue;
                }

                foreach (var pos3 in otherPoints)
                {
                    if (pos1.Key == pos3.Key || pos2.Key == pos3.Key || point.Key == pos3.Key)
                        continue;

                    var startToThird = startToSecond + otherPoints[pos2.Key][pos3.Key];

                    if (otherPoints.Count == 3)
                    {
                        possibleDistances.Add((startToThird, new List<uint> { point.Key, pos1.Key, pos2.Key, pos3.Key, }));
                        continue;
                    }

                    foreach (var pos4 in otherPoints)
                    {
                        if (pos1.Key == pos4.Key || pos2.Key == pos4.Key || pos3.Key == pos4.Key || point.Key == pos4.Key)
                            continue;

                        var startToLast = startToThird + otherPoints[pos3.Key][pos4.Key];

                        possibleDistances.Add((startToLast, new List<uint> { point.Key, pos1.Key, pos2.Key, pos3.Key, pos4.Key, }));
                    }
                }
            }
        }

        return possibleDistances.MinBy(a => a.Distance);
    }

    public static uint BestDistance(uint pointA, uint pointB)
    {
        return HousingManager.GetSubmarineVoyageDistance((byte) pointA, (byte) pointB);
    }

    public static uint VoyageTime(uint pointA, uint pointB, short speed)
    {
        return HousingManager.GetSubmarineVoyageTime((byte) pointA, (byte) pointB, speed);
    }

    public static uint SurveyTime(uint point, short speed)
    {
        return HousingManager.GetSubmarineSurveyDuration((byte) point, speed);
    }

    #endregion
}
