using System;
using Terraria.Localization;
using Terraria.GameContent.ItemDropRules;
using System.Collections.Generic;
using Terraria.ModLoader;
using Terraria;
using Microsoft.Xna.Framework;

namespace MultiHitboxNPCLibrary
{
    public class PerSegmentDropCondition : IItemDropRuleCondition
    {
        public bool CanDrop(DropAttemptInfo info)
        {
            return info.npc.GetGlobalNPC<MultiHitboxNPC>().useMultipleHitboxes;
        }

        public bool CanShowItemDropInUI()
        {
            return true;
        }

        public string GetConditionDescription()
        {
            return Language.GetTextValue("Mods.MultiHitboxNPCLibrary.DropConditions.MultiHitbox");
        }
    }

    //TODO: Drop rule from the closest segment to the player
    public class MultiHitboxDropPerSegment : IItemDropRule
    {
        public List<IItemDropRuleChainAttempt> ChainedRules { get; }

        int chanceDenominator;
        int chanceNumerator;
        int minAmount;
        int maxAmount;
        int itemId;

        public MultiHitboxDropPerSegment(int itemId, int chanceDenominator = 1, int chanceNumerator = 1, int minAmount = 1, int maxAmount = 1)
        {
            this.itemId = itemId;
            this.chanceDenominator = chanceDenominator;
            this.chanceNumerator = chanceNumerator;
            this.minAmount = minAmount;
            this.maxAmount = maxAmount;

            ChainedRules = new List<IItemDropRuleChainAttempt>();
        }

        public bool CanDrop(DropAttemptInfo info)
        {
            return info.npc.GetGlobalNPC<MultiHitboxNPC>().useMultipleHitboxes;
        }

        public void ReportDroprates(List<DropRateInfo> drops, DropRateInfoChainFeed ratesInfo)
        {
            DropRateInfoChainFeed ratesInfo2 = ratesInfo.With(1f);
            ratesInfo2.AddCondition(new PerSegmentDropCondition());

            float num = (float)chanceNumerator / (float)chanceDenominator;
            float dropRate = num * ratesInfo2.parentDroprateChance;
            drops.Add(new DropRateInfo(itemId, minAmount, maxAmount, dropRate, ratesInfo2.conditions));
            Chains.ReportDroprates(ChainedRules, num, drops, ratesInfo2);
        }

        public ItemDropAttemptResult TryDroppingItem(DropAttemptInfo info)
        {
            ItemDropAttemptResult result;

            bool success = false;
            foreach (RectangleHitbox hitbox in info.npc.GetGlobalNPC<MultiHitboxNPC>().hitboxes.AllHitboxes())
            {
                if (info.rng.Next(chanceDenominator) < chanceNumerator)
                {
                    success = true;
                    if (itemId > 0 && itemId < ItemLoader.ItemCount)
                    {
                        int itemIndex = Item.NewItem(info.npc.GetSource_Loot(), hitbox.hitbox.Center.X, hitbox.hitbox.Center.Y, 0, 0, itemId, Main.rand.Next(minAmount, maxAmount + 1), noBroadcast: false, -1);
                        CommonCode.ModifyItemDropFromNPC(info.npc, itemIndex);
                    }
                }
            }

            if (success)
            {
                result = default(ItemDropAttemptResult);
                result.State = ItemDropAttemptResultState.Success;
                return result;
            }
            result = default(ItemDropAttemptResult);
            result.State = ItemDropAttemptResultState.FailedRandomRoll;
            return result;
        }
    }
}

