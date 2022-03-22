using Barotrauma.Extensions;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace Barotrauma
{
    class PetBehavior
    {
        public enum StatusIndicatorType
        {
            None,
            Happy,
            Sad,
            Hungry
        }

        private float hunger = 50.0f;
        public float Hunger 
        { 
            get { return hunger; }
            set { hunger = MathHelper.Clamp(value, 0.0f, MaxHunger); }
        }

        private float happiness = 50.0f;
        public float Happiness
        {
            get { return happiness; }
            set { happiness = MathHelper.Clamp(value, 0.0f, MaxHappiness); }
        }

        public float MaxHappiness { get; set; }
        public float MaxHunger { get; set; }

        public float HappinessDecreaseRate { get; set; }
        public float HungerIncreaseRate { get; set; }

        public float PlayForce { get; set; }

        public float PlayTimer { get; set; }
        private float? unstunY { get; set; }

        public EnemyAIController AiController { get; private set; } = null;

        public Character Owner { get; set; }

        private class ItemProduction
        {
            public struct Item
            {
                public ItemPrefab Prefab;
                public float Commonness;
            }
            public List<Item> Items;
            public Vector2 HungerRange;
            public Vector2 HappinessRange;
            public float Rate;
            public float HungerRate;
            public float InvHungerRate;
            public float HappinessRate;
            public float InvHappinessRate;

            private readonly float totalCommonness;
            private float timer;

            public ItemProduction(XElement element)
            {
                Items = new List<Item>();

                HungerRate = element.GetAttributeFloat("hungerrate", 0.0f);
                InvHungerRate = element.GetAttributeFloat("invhungerrate", 0.0f);
                HappinessRate = element.GetAttributeFloat("happinessrate", 0.0f);
                InvHappinessRate = element.GetAttributeFloat("invhappinessrate", 0.0f);

                string[] requiredHappinessStr = element.GetAttributeString("requiredhappiness", "0-100").Split('-');
                string[] requiredHungerStr = element.GetAttributeString("requiredhunger", "0-100").Split('-');
                HappinessRange = new Vector2(0, 100);
                HungerRange = new Vector2(0, 100);
                float tempF;
                if (requiredHappinessStr.Length >= 2)
                {
                    if (float.TryParse(requiredHappinessStr[0], NumberStyles.Any, CultureInfo.InvariantCulture, out tempF)) { HappinessRange.X = tempF; }
                    if (float.TryParse(requiredHappinessStr[1], NumberStyles.Any, CultureInfo.InvariantCulture, out tempF)) { HappinessRange.Y = tempF; }
                }
                if (requiredHungerStr.Length >= 2)
                {
                    if (float.TryParse(requiredHungerStr[0], NumberStyles.Any, CultureInfo.InvariantCulture, out tempF)) { HungerRange.X = tempF; }
                    if (float.TryParse(requiredHungerStr[1], NumberStyles.Any, CultureInfo.InvariantCulture, out tempF)) { HungerRange.Y = tempF; }
                }
                Rate = element.GetAttributeFloat("rate", 0.016f);
                totalCommonness = 0.0f;
                foreach (XElement subElement in element.Elements())
                {
                    switch (subElement.Name.LocalName.ToLowerInvariant())
                    {
                        case "item":
                            string identifier = subElement.GetAttributeString("identifier", "");
                            Item newItemToProduce = new Item
                            {
                                Prefab = string.IsNullOrEmpty(identifier) ? null : ItemPrefab.Find("", subElement.GetAttributeString("identifier", "")),
                                Commonness = subElement.GetAttributeFloat("commonness", 0.0f)
                            };
                            totalCommonness += newItemToProduce.Commonness;
                            Items.Add(newItemToProduce);
                            break;
                    }
                }

                timer = 1.0f;
            }

            public void Update(PetBehavior pet, float deltaTime)
            {
                if (pet.Happiness < HappinessRange.X || pet.Happiness > HappinessRange.Y) { return; }
                if (pet.Hunger < HungerRange.X || pet.Hunger > HungerRange.Y) { return; }

                float currentRate = Rate;
                currentRate += HappinessRate * (pet.Happiness - HappinessRange.X) / (HappinessRange.Y - HappinessRange.X);
                currentRate += InvHappinessRate * (1.0f - ((pet.Happiness - HappinessRange.X) / (HappinessRange.Y - HappinessRange.X)));
                currentRate += HungerRate * (pet.Hunger - HungerRange.X) / (HungerRange.Y - HungerRange.X);
                currentRate += InvHungerRate * (1.0f - ((pet.Hunger - HungerRange.X) / (HungerRange.Y - HungerRange.X)));
                timer -= currentRate * deltaTime;
                if (timer <= 0.0f)
                {
                    timer = 1.0f;
                    float r = Rand.Range(0.0f, totalCommonness);
                    float aggregate = 0.0f;
                    for (int i = 0; i < Items.Count; i++)
                    {
                        aggregate += Items[i].Commonness;
                        if (aggregate >= r && Items[i].Prefab != null)
                        {
                            GameAnalyticsManager.AddDesignEvent("MicroInteraction:" + (GameMain.GameSession?.GameMode?.Preset.Identifier ?? "null") + ":PetProducedItem:" + pet.AiController.Character.SpeciesName + ":" + Items[i].Prefab.Identifier);
                            Entity.Spawner.AddToSpawnQueue(Items[i].Prefab, pet.AiController.Character.WorldPosition);
                            break;
                        }
                    }
                }
            }
        }

        private class Food
        {
            public string Tag;
            public Vector2 HungerRange;
            public float Hunger;
            public float Happiness;
            public float Priority;
            public bool IgnoreContained;

            public CharacterParams.TargetParams TargetParams = null;
        }

        private readonly List<ItemProduction> itemsToProduce = new List<ItemProduction>();
        private readonly List<Food> foods = new List<Food>();

        public PetBehavior(XElement element, EnemyAIController aiController)
        {
            AiController = aiController;
            AiController.Character.CanBeDragged = true;

            MaxHappiness = element.GetAttributeFloat("maxhappiness", 100.0f);
            MaxHunger = element.GetAttributeFloat("maxhunger", 100.0f);

            Happiness = MaxHappiness * 0.5f;
            Hunger = MaxHunger * 0.5f;

            HappinessDecreaseRate = element.GetAttributeFloat("happinessdecreaserate", 0.1f);
            HungerIncreaseRate = element.GetAttributeFloat("hungerincreaserate", 0.25f);

            PlayForce = element.GetAttributeFloat("playforce", 15.0f);

            foreach (XElement subElement in element.Elements())
            {
                switch (subElement.Name.LocalName.ToLowerInvariant())
                {
                    case "itemproduction":
                        itemsToProduce.Add(new ItemProduction(subElement));
                        break;
                    case "eat":
                        Food food = new Food
                        {
                            Tag = subElement.GetAttributeString("tag", ""),
                            Hunger = subElement.GetAttributeFloat("hunger", -1),
                            Happiness = subElement.GetAttributeFloat("happiness", 1),
                            Priority = subElement.GetAttributeFloat("priority", 100),
                            IgnoreContained = subElement.GetAttributeBool("ignorecontained", true)
                        };
                        string[] requiredHungerStr = subElement.GetAttributeString("requiredhunger", "0-100").Split('-');
                        food.HungerRange = new Vector2(0, 100);
                        if (requiredHungerStr.Length >= 2)
                        {
                            if (float.TryParse(requiredHungerStr[0], NumberStyles.Any, CultureInfo.InvariantCulture, out float tempF)) { food.HungerRange.X = tempF; }
                            if (float.TryParse(requiredHungerStr[1], NumberStyles.Any, CultureInfo.InvariantCulture, out tempF)) { food.HungerRange.Y = tempF; }
                        }
                        foods.Add(food);
                        break;
                }
            }

            GameAnalyticsManager.AddDesignEvent("MicroInteraction:" + (GameMain.GameSession?.GameMode?.Preset.Identifier ?? "null") + ":PetSpawned:" + aiController.Character.SpeciesName);
        }

        public StatusIndicatorType GetCurrentStatusIndicatorType()
        {
            if (Hunger > MaxHunger * 0.5f) { return StatusIndicatorType.Hungry; }
            if (Happiness > MaxHappiness * 0.8f) { return StatusIndicatorType.Happy; }
            if (Happiness < MaxHappiness * 0.25f) { return StatusIndicatorType.Sad; }
            return StatusIndicatorType.None;
        }

        public bool OnEat(Item item)
        {
            bool success = OnEat(item.GetTags());
            if (success)
            {
                GameAnalyticsManager.AddDesignEvent("MicroInteraction:" + (GameMain.GameSession?.GameMode?.Preset.Identifier ?? "null") + ":PetEat:" + AiController.Character.SpeciesName + ":" + item.prefab.Identifier);
            }
            return success;
        }

        public bool OnEat(Character character)
        {
            if (character == null || !character.IsDead) { return false; }
            bool success = OnEat("dead");
            if (success)
            {
                GameAnalyticsManager.AddDesignEvent("MicroInteraction:" + (GameMain.GameSession?.GameMode?.Preset.Identifier ?? "null") + ":PetEat:" + AiController.Character.SpeciesName + ":" + character.SpeciesName);
            }
            return success;
        }

        private bool OnEat(IEnumerable<string> tags)
        {
            foreach (string tag in tags)
            {
                if (OnEat(tag)) { return true; }
            }
            return false;
        }

        private bool OnEat(string tag)
        {
            for (int i = 0; i < foods.Count; i++)
            {
                if (tag.Equals(foods[i].Tag, System.StringComparison.OrdinalIgnoreCase))
                {
                    Hunger += foods[i].Hunger;
                    Happiness += foods[i].Happiness;
#if CLIENT
                    AiController.Character.PlaySound(CharacterSound.SoundType.Happy, 0.5f);
#endif
                    return true;
                }
            }
            return false;
        }

        public void Play(Character player)
        {
            if (PlayTimer > 0.0f) { return; }
            if (Owner == null) { Owner = player; }
            PlayTimer = 5.0f;
            AiController.Character.IsRagdolled = true;
            Happiness += 10.0f;
            AiController.Character.AnimController.MainLimb.body.LinearVelocity += new Vector2(0, PlayForce);
            unstunY = AiController.Character.SimPosition.Y;
#if CLIENT
            AiController.Character.PlaySound(CharacterSound.SoundType.Happy, 0.9f);
#endif
        }

        public string GetTagName()
        {
            if (AiController.Character.Inventory != null)
            {
                foreach (Item item in AiController.Character.Inventory.AllItems)
                {
                    var tag = item.GetComponent<NameTag>();
                    if (tag != null && !string.IsNullOrWhiteSpace(tag.WrittenName))
                    {
                        return tag.WrittenName;
                    }
                }
            }

            return string.Empty;
        }

        public void Update(float deltaTime)
        {
            var character = AiController.Character;
            if (character?.Removed ?? true || character.IsDead) { return; }

            if (unstunY.HasValue)
            {
                if (PlayTimer > 4.0f)
                {
                    float extent = character.AnimController.MainLimb.body.GetMaxExtent();
                    if (character.SimPosition.Y < (unstunY.Value + extent * 3.0f) &&
                        character.AnimController.MainLimb.body.LinearVelocity.Y < 0.0f)
                    {
                        character.IsRagdolled = false;
                        unstunY = null;
                    }
                    else
                    {
                        character.IsRagdolled = true;
                    }
                }
                else
                {
                    character.IsRagdolled = false;
                    unstunY = null;
                }
            }

            PlayTimer -= deltaTime;

            if (GameMain.NetworkMember?.IsClient ?? false) { return; }
            if (Owner != null && (Owner.Removed || Owner.IsDead)) { Owner = null; }

            Hunger += HungerIncreaseRate * deltaTime;
            Happiness -= HappinessDecreaseRate * deltaTime;

            for (int i = 0; i < foods.Count; i++)
            {
                Food food = foods[i];
                if (Hunger >= food.HungerRange.X && Hunger <= food.HungerRange.Y)
                {
                    if (food.TargetParams == null &&
                        AiController.AIParams.TryAddNewTarget(food.Tag, AIState.Eat, food.Priority, out CharacterParams.TargetParams targetParams))
                    {
                        targetParams.IgnoreContained = food.IgnoreContained;
                        food.TargetParams = targetParams;
                    }
                }
                else if (food.TargetParams != null)
                {
                    AiController.AIParams.RemoveTarget(food.TargetParams);
                    food.TargetParams = null;
                }
            }

            if (Hunger >= MaxHunger * 0.99f)
            {
                character.CharacterHealth.ApplyAffliction(character.AnimController.MainLimb, new Affliction(AfflictionPrefab.InternalDamage, 8.0f * deltaTime));
            }
            else if (Hunger < MaxHunger * 0.1f)
            {
                character.CharacterHealth.ReduceAffliction(null, null, 8.0f * deltaTime);
            }

            if (character.SelectedBy != null)
            {
                character.IsRagdolled = true;
                unstunY = character.SimPosition.Y;
            }

            for (int i = 0; i < itemsToProduce.Count; i++)
            {
                itemsToProduce[i].Update(this, deltaTime);
            }
        }

        public static void SavePets(XElement petsElement)
        {
            foreach (Character c in Character.CharacterList)
            {
                if (!c.IsPet || c.IsDead) { continue; }
                if (c.Submarine == null) { continue; }

                var petBehavior = (c.AIController as EnemyAIController)?.PetBehavior;
                if (petBehavior == null) { continue; }

                XElement petElement = new XElement("pet", 
                    new XAttribute("speciesname", c.SpeciesName), 
                    new XAttribute("ownerhash", petBehavior.Owner?.Info?.GetIdentifier() ?? 0),
                    new XAttribute("seed", c.Seed));

                var petBehaviorElement = new XElement("petbehavior",
                    new XAttribute("hunger", petBehavior.Hunger.ToString("G", CultureInfo.InvariantCulture)),
                    new XAttribute("happiness", petBehavior.Happiness.ToString("G", CultureInfo.InvariantCulture)));
                petElement.Add(petBehaviorElement);

                var healthElement = new XElement("health");
                c.CharacterHealth.Save(healthElement);
                petElement.Add(healthElement);

                if (c.Inventory != null)
                {
                    var inventoryElement = new XElement("inventory");
                    Character.SaveInventory(c.Inventory, inventoryElement);
                    petElement.Add(inventoryElement);
                }

                petsElement.Add(petElement);
            }
        }

        public static void LoadPets(XElement petsElement)
        {
            foreach (XElement subElement in petsElement.Elements())
            {
                string speciesName = subElement.GetAttributeString("speciesname", "");
                string seed = subElement.GetAttributeString("seed", "123");
                int ownerHash = subElement.GetAttributeInt("ownerhash", 0);
                Vector2 spawnPos = Vector2.Zero;
                Character owner = Character.CharacterList.Find(c => c.Info?.GetIdentifier() == ownerHash);
                if (owner != null && owner.Submarine?.Info.Type == SubmarineType.Player)
                {
                    spawnPos = owner.WorldPosition;
                }
                else
                {
                    //try to find a spawnpoint in the main sub
                    var spawnPoint = WayPoint.WayPointList.Where(wp => wp.SpawnType == SpawnType.Human && wp.Submarine == Submarine.MainSub).GetRandom();
                    //if not found, try any player sub (shuttle/drone etc)
                    spawnPoint ??= WayPoint.WayPointList.Where(wp => wp.SpawnType == SpawnType.Human && wp.Submarine?.Info.Type == SubmarineType.Player).GetRandom();
                    spawnPos = spawnPoint?.WorldPosition ?? Submarine.MainSub.WorldPosition;
                }
                var pet = Character.Create(speciesName, spawnPos, seed);
                var petBehavior = (pet?.AIController as EnemyAIController)?.PetBehavior;
                if (petBehavior != null)
                {
                    petBehavior.Owner = owner;
                    var petBehaviorElement = subElement.Element("petbehavior");
                    if (petBehaviorElement != null)
                    {
                        petBehavior.Hunger = petBehaviorElement.GetAttributeFloat("hunger", 50.0f);
                        petBehavior.Happiness = petBehaviorElement.GetAttributeFloat("happiness", 50.0f);
                    }
                }

                var inventoryElement = subElement.Element("inventory");
                if (inventoryElement != null)
                {
                    pet.SpawnInventoryItems(pet.Inventory, inventoryElement);
                }
            }
        }

        public void ServerWrite(IWriteMessage msg)
        {
            msg.WriteRangedSingle(Happiness, 0.0f, MaxHappiness, 8);
            msg.WriteRangedSingle(Hunger, 0.0f, MaxHunger, 8);
        }

        public void ClientRead(IReadMessage msg)
        {
            Happiness = msg.ReadRangedSingle(0.0f, MaxHappiness, 8);
            Hunger = msg.ReadRangedSingle(0.0f, MaxHunger, 8);
        }
    }
}
