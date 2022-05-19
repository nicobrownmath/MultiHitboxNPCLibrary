using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.Graphics.Shaders;
using static Terraria.ModLoader.ModContent;
using Terraria.GameInput;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.IO;
using System.Collections.Generic;
using MonoMod.Cil;
using Terraria.ModLoader.IO;
using Terraria.Enums;
using Terraria.GameContent.ItemDropRules;
using Terraria.Localization;
using static Terraria.GameContent.ItemDropRules.Chains;
using System.Reflection;
using Mono.Cecil.Cil;
using Terraria.GameContent;
using Terraria.Utilities;
using MonoMod.RuntimeDetour.HookGen;

namespace MultiHitboxNPCLibrary
{
	public class MultiHitboxNPCLibrary : Mod
	{
        public override void HandlePacket(BinaryReader reader, int whoAmI)
        {
            switch (reader.ReadString())
            {
                case "MultiHitboxNPCLibrary:SyncNPC":
                    NPC npc = Main.npc[reader.ReadInt32()];
                    if (npc.active)
                    {
                        npc.GetGlobalNPC<MultiHitboxNPC>().hitboxes = MultiHitbox.Read(reader);
                        npc.GetGlobalNPC<MultiHitboxNPC>().hitboxes.Refresh();
                    }
                    break;
            }
        }
    }
}