using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Pathoschild.Stardew.TractorMod.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using SFarmer = StardewValley.Farmer;
using SObject = StardewValley.Object;

namespace Pathoschild.Stardew.TractorMod
{
    public class TractorMod : Mod
    {
        /*********
        ** Properties
        *********/
        /****
        ** Constants
        ****/
        /// <summary>The tractor garage's building type.</summary>
        private readonly string GarageBuildingType = "TractorGarage";

        /// <summary>The full type name for the Pelican Fiber mod's construction menu.</summary>
        private readonly string PelicanFiberMenuFullName = "PelicanFiber.Framework.ConstructionMenu";

        /// <summary>The unique buff ID for the tractor speed.</summary>
        private readonly int BuffUniqueID = 58012397;

        /// <summary>The number of ticks between each tractor action check.</summary>
        private readonly int TicksPerAction = 12; // roughly five times per second

        /// <summary>The number of days needed to build a tractor garage.</summary>
        private readonly int GarageConstructionDays = 3;

        /****
        ** State
        ****/
        /// <summary>The mod settings.</summary>
        private ModConfig Config;

        /// <summary>The current player's farm.</summary>
        private Farm Farm;

        /// <summary>Manages the tractor instance.</summary>
        private TractorManager Tractor;

        /// <summary>Whether Robin is busy constructing a garage.</summary>
        private bool IsRobinBusy;

        /// <summary>The number of ticks since the tractor last checked for an action to perform.</summary>
        private int SkippedActionTicks;

        /// <summary>The tractor garages which started construction today.</summary>
        private readonly List<Building> GaragesStartedToday = new List<Building>();


        /*********
        ** Public methods
        *********/
        /// <summary>The mod entry point, called after the mod is first loaded.</summary>
        /// <param name="helper">Provides simplified APIs for writing mods.</param>
        public override void Entry(IModHelper helper)
        {
            // read config
            this.MigrateLegacySaveData(helper);
            this.Config = helper.ReadConfig<ModConfig>();

            // spawn/unspawn tractor and garages
            TimeEvents.AfterDayStarted += this.TimeEvents_AfterDayStarted;
            SaveEvents.BeforeSave += this.SaveEvents_BeforeSave;

            // add blueprint to Robin's shop
            MenuEvents.MenuChanged += this.MenuEvents_MenuChanged;

            // handle player interaction & tractor logic
            ControlEvents.KeyPressed += this.ControlEvents_KeyPressed;
            GameEvents.UpdateTick += this.GameEvents_UpdateTick;
        }


        /*********
        ** Private methods
        *********/
        /****
        ** Event handlers
        ****/
        /// <summary>The event called when a new day begins.</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        private void TimeEvents_AfterDayStarted(object sender, EventArgs e)
        {
            // set up for new day
            this.Tractor = null;
            this.GaragesStartedToday.Clear();
            this.Farm = Game1.getFarm();
            this.RestoreCustomData();
        }

        /// <summary>The event called before the game starts saving.</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        private void SaveEvents_BeforeSave(object sender, EventArgs e)
        {
            this.StashCustomData();
        }

        /// <summary>The event called after a new menu is opened.</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        private void MenuEvents_MenuChanged(object sender, EventArgsClickableMenuChanged e)
        {
            // add blueprint to carpenter menu
            if (e.NewMenu is CarpenterMenu)
            {
                this.Helper.Reflection
                    .GetPrivateValue<List<BluePrint>>(e.NewMenu, "blueprints")
                    .Add(this.GetBlueprint());
            }
            else if (e.NewMenu.GetType().FullName == this.PelicanFiberMenuFullName)
            {
                this.Helper.Reflection
                    .GetPrivateValue<List<BluePrint>>(e.NewMenu, "Blueprints")
                    .Add(this.GetBlueprint());
            }
        }

        /// <summary>The event called when the player presses a keyboard button.</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        private void ControlEvents_KeyPressed(object sender, EventArgsKeyPressed e)
        {
            // summon tractor
            if (e.KeyPressed == this.Config.TractorKey)
                this.Tractor?.SetLocation(Game1.currentLocation, Game1.player.getTileLocation());
        }

        /// <summary>The event called when the game updates (roughly sixty times per second).</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        private void GameEvents_UpdateTick(object sender, EventArgs e)
        {
            if (Game1.activeClickableMenu is CarpenterMenu || Game1.activeClickableMenu?.GetType().FullName == this.PelicanFiberMenuFullName)
                this.ProcessNewConstruction();
            if (Game1.currentLocation != null)
                this.Update();
        }

        /****
        ** State methods
        ****/
        /// <summary>Detect and fix tractor garages that started construction today.</summary>
        private void ProcessNewConstruction()
        {
            foreach (Building garage in this.GetGarages(this.Farm).Keys)
            {
                // skip if not built today
                if (garage is TractorGarage)
                    continue;

                // set construction days after it's placed
                if (!this.GaragesStartedToday.Contains(garage))
                {
                    garage.daysOfConstructionLeft = this.GarageConstructionDays;
                    this.GaragesStartedToday.Add(garage);
                }

                // spawn tractor if built instantly by a mod
                if (!garage.isUnderConstruction())
                {
                    this.GaragesStartedToday.Remove(garage);
                    this.Farm.destroyStructure(garage);
                    this.Farm.buildings.Add(new TractorGarage(this.GetBlueprint(), new Vector2(garage.tileX, garage.tileY), 0));
                    if (this.Tractor == null)
                        this.Tractor = this.SpawnTractor(garage.tileX + 1, garage.tileY + 1);
                }
            }
        }

        /// <summary>Spawn a new tractor.</summary>
        /// <param name="tileX">The tile X position at which to spawn it.</param>
        /// <param name="tileY">The tile Y position at which to spawn it.</param>
        private TractorManager SpawnTractor(int tileX, int tileY)
        {
            TractorManager tractor = new TractorManager("Tractor", tileX, tileY, this.Helper.Content);
            tractor.SetLocation(this.Farm, new Vector2(tileX, tileY));
            tractor.SetPixelPosition(new Vector2(tractor.Current.Position.X + 20, tractor.Current.Position.Y));
            return tractor;
        }


        /****
        ** Save methods
        ****/
        /// <summary>Get the mod-relative path for custom save data.</summary>
        /// <param name="saveID">The save ID.</param>
        private string GetDataPath(string saveID)
        {
            return $"data/{saveID}.json";
        }

        /// <summary>Stash all tractor and garage data to a separate file to avoid breaking the save file.</summary>
        private void StashCustomData()
        {
            // back up garages
            IDictionary<Building, CustomSaveBuilding> garages = this.GetGarages(this.Farm);
            CustomSaveData saveData = new CustomSaveData(garages.Values);
            this.Helper.WriteJsonFile(this.GetDataPath(Constants.SaveFolderName), saveData);

            // remove tractors + buildings
            foreach (Building garage in garages.Keys)
                this.Farm.destroyStructure(garage);
            this.Tractor?.RemoveTractors();

            // reset Robin construction
            if (this.IsRobinBusy)
            {
                this.IsRobinBusy = false;
                NPC robin = Game1.getCharacterFromName("Robin");
                robin.ignoreScheduleToday = false;
                robin.CurrentDialogue.Clear();
                robin.dayUpdate(Game1.dayOfMonth);
            }
        }

        /// <summary>Restore tractor and garage data removed by <see cref="StashCustomData"/>.</summary>
        /// <remarks>The Robin construction logic is derived from <see cref="NPC.reloadSprite"/> and <see cref="StardewValley.Farm.resetForPlayerEntry"/>.</remarks>
        private void RestoreCustomData()
        {
            // get save data
            CustomSaveData saveData = this.Helper.ReadJsonFile<CustomSaveData>(this.GetDataPath(Constants.SaveFolderName));
            if (saveData?.Buildings == null)
                return;

            // add tractor + garages
            BluePrint blueprint = this.GetBlueprint();
            foreach (CustomSaveBuilding garageData in saveData.Buildings)
            {
                // add garage
                TractorGarage garage = new TractorGarage(blueprint, garageData.Tile, Math.Max(0, garageData.DaysOfConstructionLeft - 1));
                this.Farm.buildings.Add(garage);

                // add Robin construction
                if (garage.isUnderConstruction() && !this.IsRobinBusy)
                {
                    this.IsRobinBusy = true;
                    NPC robin = Game1.getCharacterFromName("Robin");

                    // update Robin
                    robin.ignoreMultiplayerUpdates = true;
                    robin.sprite.setCurrentAnimation(new List<FarmerSprite.AnimationFrame>
                    {
                        new FarmerSprite.AnimationFrame(24, 75),
                        new FarmerSprite.AnimationFrame(25, 75),
                        new FarmerSprite.AnimationFrame(26, 300, false, false, farmer => this.Helper.Reflection.GetPrivateMethod(robin,"robinHammerSound").Invoke(farmer)),
                        new FarmerSprite.AnimationFrame(27, 1000, false, false, farmer => this.Helper.Reflection.GetPrivateMethod(robin,"robinVariablePause").Invoke(farmer))
                    });
                    robin.ignoreScheduleToday = true;
                    Game1.warpCharacter(robin, this.Farm.Name, new Vector2(garage.tileX + garage.tilesWide / 2, garage.tileY + garage.tilesHigh / 2), false, false);
                    robin.position.X += Game1.tileSize / 4;
                    robin.position.Y -= Game1.tileSize / 2;
                    robin.CurrentDialogue.Clear();
                    robin.CurrentDialogue.Push(new Dialogue(Game1.content.LoadString("Strings\\StringsFromCSFiles:NPC.cs.3926"), robin));
                }

                // spawn tractor
                if (this.Tractor == null && !garage.isUnderConstruction())
                {
                    this.Tractor = this.SpawnTractor(garage.tileX + 1, garage.tileY + 1);
                    break;
                }
            }
        }

        /// <summary>Migrate the legacy <c>TractorModSave.json</c> file to the new config files.</summary>
        /// <param name="helper">Provides simplified APIs for writing mods.</param>
        private void MigrateLegacySaveData(IModHelper helper)
        {
            // get file
            const string filename = "TractorModSave.json";
            FileInfo file = new FileInfo(Path.Combine(helper.DirectoryPath, filename));
            if (!file.Exists)
                return;

            // read legacy data
            this.Monitor.Log($"Found legacy {filename}, migrating to new save data...");
            IDictionary<string, CustomSaveData> saves = new Dictionary<string, CustomSaveData>();
            {
                LegacySaveData data = helper.ReadJsonFile<LegacySaveData>(filename);
                if (data.Saves != null && data.Saves.Any())
                {
                    foreach (LegacySaveData.LegacySaveEntry saveData in data.Saves)
                    {
                        saves[$"{saveData.FarmerName}_{saveData.SaveSeed}"] = new CustomSaveData(
                            saveData.TractorHouse.Select(p => new CustomSaveBuilding(new Vector2(p.X, p.Y), this.GarageBuildingType, 0))
                        );
                    }
                }
            }

            // write new files
            foreach (var save in saves)
            {
                if (save.Value.Buildings.Any())
                    helper.WriteJsonFile(this.GetDataPath(save.Key), save.Value);
            }

            // delete old file
            file.Delete();
        }

        /****
        ** Action methods
        ****/
        /// <summary>Update tractor effects and actions in the game.</summary>
        private void Update()
        {
            if (Game1.player == null || this.Tractor?.IsRiding != true || Game1.activeClickableMenu != null)
                return; // tractor isn't enabled

            // apply tractor speed buff
            Buff speedBuff = Game1.buffsDisplay.otherBuffs.FirstOrDefault(p => p.which == this.BuffUniqueID);
            if (speedBuff == null)
            {
                speedBuff = new Buff(0, 0, 0, 0, 0, 0, 0, 0, 0, this.Config.TractorSpeed, 0, 0, 1, "Tractor Power", this.Helper.Translation.Get("buff.name")) { which = this.BuffUniqueID };
                Game1.buffsDisplay.addOtherBuff(speedBuff);
            }
            speedBuff.millisecondsDuration = 100;

            // apply action cooldown
            this.SkippedActionTicks++;
            if (this.SkippedActionTicks % this.TicksPerAction != 0)
                return;
            this.SkippedActionTicks = 0;

            // perform tractor action
            Tool tool = Game1.player.CurrentTool;
            Item item = Game1.player.CurrentItem;
            Vector2[] grid = this.GetTileGrid(Game1.player.getTileLocation(), this.Config.Distance).ToArray();
            if (tool is MeleeWeapon && tool.name.ToLower().Contains("scythe"))
                this.HarvestTiles(grid);
            else if (tool != null)
                this.ApplyTool(tool, grid);
            else if (item != null)
                this.ApplyItem(item, grid);
        }

        /// <summary>Apply an item stack to the given tiles.</summary>
        /// <param name="item">The item stack to apply.</param>
        /// <param name="tiles">The tiles to affect.</param>
        private void ApplyItem(Item item, Vector2[] tiles)
        {
            // validate category
            string category = item.getCategoryName().ToLower();
            if (category != "seed" && category != "fertilizer")
                return;

            // act on affected tiles
            foreach (Vector2 tile in tiles)
            {
                // get tilled dirt
                if (!Game1.currentLocation.terrainFeatures.TryGetValue(tile, out TerrainFeature terrainTile) || !(terrainTile is HoeDirt dirt))
                    continue;

                // apply item
                bool applied = false;
                switch (category)
                {
                    case "seed":
                        if (dirt.crop == null && dirt.plant(Game1.player.CurrentItem.parentSheetIndex, (int)tile.X, (int)tile.Y, Game1.player))
                            applied = true;
                        break;

                    case "fertilizer":
                        if (dirt.fertilizer == 0)
                        {
                            dirt.fertilizer = Game1.player.CurrentItem.parentSheetIndex;
                            applied = true;
                        }
                        break;

                    default:
                        throw new NotSupportedException($"Unknown category '{category}'.");
                }

                // deduct from inventory
                if (applied)
                {
                    Game1.player.CurrentItem.Stack -= 1;
                    if (Game1.player.CurrentItem.Stack <= 0)
                    {
                        Game1.player.removeItemFromInventory(Game1.player.CurrentItem);
                        return;
                    }
                }
            }
        }

        /// <summary>Harvest the affected tiles.</summary>
        /// <param name="tiles">The tiles to harvest.</param>
        private void HarvestTiles(Vector2[] tiles)
        {
            if (!this.Config.ScytheHarvests)
                return;

            foreach (Vector2 tile in tiles)
            {
                // get feature/object on tile
                object target;
                {
                    if (Game1.currentLocation.terrainFeatures.TryGetValue(tile, out TerrainFeature feature))
                        target = feature;
                    else if (Game1.currentLocation.objects.TryGetValue(tile, out SObject obj))
                        target = obj;
                    else
                        continue;
                }

                // harvest target
                switch (target)
                {
                    // crop or spring onion
                    case HoeDirt dirt when dirt.crop != null:
                    {
                        // make item scythe-harvestable
                        int oldHarvestMethod = dirt.crop.harvestMethod;
                        dirt.crop.harvestMethod = Crop.sickleHarvest;

                        // harvest spring onion
                        if (dirt.crop.whichForageCrop == Crop.forageCrop_springOnion)
                        {
                            SObject onion = new SObject(399, 1);
                            bool gatherer = Game1.player.professions.Contains(SFarmer.gatherer);
                            bool botanist = Game1.player.professions.Contains(SFarmer.botanist);
                            if (botanist)
                                onion.quality = SObject.bestQuality;
                            if (gatherer)
                            {
                                if (new Random().Next(0, 10) < 2)
                                    onion.stack *= 2;
                            }
                            for (int i = 0; i < onion.stack; i++)
                                Game1.currentLocation.debris.Add(new Debris(onion, new Vector2(tile.X * Game1.tileSize, tile.Y * Game1.tileSize)));

                            dirt.destroyCrop(tile);
                            continue;
                        }

                        // harvest crop
                        if (dirt.crop.harvest((int)tile.X, (int)tile.Y, dirt))
                        {
                            if (dirt.crop.indexOfHarvest == 421) // sun flower
                            {
                                int seedDrop = new Random().Next(1, 4);
                                for (int i = 0; i < seedDrop; i++)
                                    Game1.createObjectDebris(431, (int)tile.X, (int)tile.Y, -1, 0, 1f, Game1.currentLocation); // spawn sunflower seeds
                            }

                            if (dirt.crop.regrowAfterHarvest == -1)
                                dirt.destroyCrop(tile);
                        }

                        // restore item harvest type
                        if (dirt.crop != null)
                            dirt.crop.harvestMethod = oldHarvestMethod;
                        break;
                    }

                    // fruit tree
                    case FruitTree tree:
                        tree.shake(tile, false);
                        break;

                    // grass
                    case Grass _:
                        Game1.currentLocation.terrainFeatures.Remove(tile);
                        this.Farm.tryToAddHay(2);
                        break;

                    // spawned object
                    case SObject obj when obj.isSpawnedObject:
                        // get output
                        if (obj.isForage(Game1.currentLocation))
                        {
                            bool gatherer = Game1.player.professions.Contains(SFarmer.gatherer);
                            bool botanist = Game1.player.professions.Contains(SFarmer.botanist);
                            if (botanist)
                                obj.quality = SObject.bestQuality;
                            if (gatherer)
                            {
                                int num = new Random().Next(0, 100);
                                if (num < 20)
                                    obj.stack *= 2;
                            }
                        }

                        // spawn output
                        for (int i = 0; i < obj.stack; i++)
                            Game1.currentLocation.debris.Add(new Debris(obj, new Vector2(tile.X * Game1.tileSize, tile.Y * Game1.tileSize)));

                        // remove harvested object
                        Game1.currentLocation.removeObject(tile, false);
                        break;

                    // weed
                    case SObject obj when obj.name.ToLower().Contains("weed"):
                        Game1.createObjectDebris(771, (int)tile.X, (int)tile.Y, -1, 0, 1f, Game1.currentLocation); // fiber
                        if (new Random().Next(0, 10) < 1)
                            Game1.createObjectDebris(770, (int)tile.X, (int)tile.Y, -1, 0, 1f, Game1.currentLocation); // 10% mixed seeds
                        Game1.currentLocation.removeObject(tile, false);
                        break;
                }
            }
        }

        /// <summary>Use a tool on the given tiles.</summary>
        /// <param name="tool">The tool to use.</param>
        /// <param name="tiles">The tiles to affect.</param>
        private void ApplyTool(Tool tool, Vector2[] tiles)
        {
            // check if tool is enabled
            if (!this.Config.CustomTools.Contains(tool.name))
            {
                switch (tool)
                {
                    case WateringCan _:
                        if (!this.Config.WateringCanWaters)
                            return;
                        break;

                    case Hoe _:
                        if (!this.Config.HoeTillsDirt)
                            return;
                        break;

                    case Pickaxe _:
                        if (!this.Config.PickaxeClearsDirt && !this.Config.PickaxeBreaksRocks && !this.Config.PickaxeBreaksFlooring)
                            return; // nothing to do
                        break;

                    default:
                        return;
                }
            }

            // track things that shouldn't decrease
            WateringCan wateringCan = tool as WateringCan;
            int waterInCan = wateringCan?.WaterLeft ?? 0;
            float stamina = Game1.player.stamina;
            int toolUpgrade = tool.upgradeLevel;
            Vector2 mountPosition = this.Tractor.Current.position;

            // use tools
            this.Tractor.Current.position = new Vector2(0, 0);
            if (wateringCan != null)
                wateringCan.WaterLeft = wateringCan.waterCanMax;
            tool.upgradeLevel = Tool.iridium;
            Game1.player.toolPower = 0;
            foreach (Vector2 tile in tiles)
            {
                Game1.currentLocation.objects.TryGetValue(tile, out SObject tileObj);
                Game1.currentLocation.terrainFeatures.TryGetValue(tile, out TerrainFeature tileFeature);

                // prevent tools from destroying placed objects
                if (tileObj != null && tileObj.Name != "Stone")
                {
                    if (tool is Hoe || tool is Pickaxe)
                        continue;
                }

                // prevent pickaxe from destroying
                if (tool is Pickaxe)
                {
                    // never destroy live crops
                    if (tileFeature is HoeDirt dirt && dirt.crop != null && !dirt.crop.dead)
                        continue;

                    // don't destroy other things unless configured
                    if (!this.Config.PickaxeBreaksFlooring && tileFeature is Flooring)
                        continue;
                    if (!this.Config.PickaxeClearsDirt && tileFeature is HoeDirt)
                        continue;
                    if (!this.Config.PickaxeBreaksRocks && tileObj?.Name == "Stone")
                        continue;
                }

                // use tool on center of tile
                Vector2 useAt = (tile * Game1.tileSize) + new Vector2(Game1.tileSize / 2f);
                tool.DoFunction(Game1.currentLocation, (int)useAt.X, (int)useAt.Y, 0, Game1.player);
            }

            // reset tools
            this.Tractor.Current.position = mountPosition;
            if (wateringCan != null)
                wateringCan.WaterLeft = waterInCan;
            tool.upgradeLevel = toolUpgrade;
            Game1.player.stamina = stamina;
        }

        /****
        ** Helper methods
        ****/
        /// <summary>Get a grid of tiles.</summary>
        /// <param name="origin">The center of the grid.</param>
        /// <param name="distance">The number of tiles in each direction to include.</param>
        private IEnumerable<Vector2> GetTileGrid(Vector2 origin, int distance)
        {
            for (int x = -distance; x <= distance; x++)
            {
                for (int y = -distance; y <= distance; y++)
                    yield return new Vector2(origin.X + x, origin.Y + y);
            }
        }

        /// <summary>Get garages in the given location to save.</summary>
        /// <param name="location">The location to search.</param>
        private IDictionary<Building, CustomSaveBuilding> GetGarages(BuildableGameLocation location)
        {
            return
                (
                    from building in location.buildings
                    where building.buildingType == this.GarageBuildingType
                    select new { Key = building, Value = new CustomSaveBuilding(new Vector2(building.tileX, building.tileY), this.GarageBuildingType, building.daysOfConstructionLeft) }
                )
                .ToDictionary(p => p.Key, p => p.Value);
        }

        /// <summary>Get a blueprint to construct the tractor garage.</summary>
        private BluePrint GetBlueprint()
        {
            return new BluePrint(this.GarageBuildingType)
            {
                name = this.GarageBuildingType,
                texture = this.Helper.Content.Load<Texture2D>(@"assets\TractorHouse.png"),
                humanDoor = new Point(-1, -1),
                animalDoor = new Point(-2, -1),
                mapToWarpTo = null,
                displayName = this.Helper.Translation.Get("garage.name"),
                description = this.Helper.Translation.Get("garage.description"),
                blueprintType = "Buildings",
                nameOfBuildingToUpgrade = null,
                actionBehavior = null,
                maxOccupants = -1,
                moneyRequired = this.Config.BuildPrice,
                tilesWidth = 4,
                tilesHeight = 2,
                sourceRectForMenuView = new Rectangle(0, 0, 64, 96),
                itemsRequired = this.Config.BuildUsesResources
                    ? new Dictionary<int, int> { [SObject.ironBar] = 20, [SObject.iridiumBar] = 5, [787/* battery pack */] = 5 }
                    : new Dictionary<int, int>(),
                namesOfOkayBuildingLocations = new List<string> { "Farm" }
            };
        }
    }
}
