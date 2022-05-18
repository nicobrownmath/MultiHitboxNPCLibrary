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

namespace MultiHitboxNPCLibrary
{
    //TODO: I should probably provide some documentation
    //TODO: Javelins and other sticky projectiles break with MultiHitboxNPCs
    //I don't really see how to fix this one without a full rewrite of Javelin code
    //This doesn't have much impact on their functionality so it's not a major issue, but I would like to fix it at some point
    //TODO: Produce death sound from (real center? the closest center to the player?)
    //TODO: Mouse hovering
    //TODO: Multiplayer syncing
    //TODO: Possibly make effects like spectre healing come from the hit segment
    public class MultiHitboxNPC : GlobalNPC
    {
        public override bool InstancePerEntity => true;

        public bool useMultipleHitboxes => hitboxes != null;
        public ANPCHitbox hitboxes;
        public RectangleHitbox mostRecentHitbox;

        public int widthForInteractions;
        public int heightForInteractions;

        Vector2 preModifyDataCenter;
        int preModifyDataWidth;
        int preModifyDataHeight;
        bool doDataReset;

        public bool doDebugDraws;

        public override void Load()
        {
            On.Terraria.Collision.CheckAABBvLineCollision_Vector2_Vector2_Vector2_Vector2_float_refSingle += Collision_CheckAABBvLineCollision_Vector2_Vector2_Vector2_Vector2_float_refSingle;

            On.Terraria.Player.ItemCheck_MeleeHitNPCs += Player_ItemCheck_MeleeHitNPCs;
            On.Terraria.NPC.Collision_LavaCollision += NPC_Collision_LavaCollision;
            On.Terraria.NPC.UpdateNPC_BuffSetFlags += NPC_UpdateNPC_BuffSetFlags;
            On.Terraria.NPC.UpdateNPC_BuffApplyVFX += NPC_UpdateNPC_BuffApplyVFX;
            On.Terraria.NPC.UpdateCollision += NPC_UpdateCollision;
            On.Terraria.NPC.StrikeNPC += NPC_StrikeNPC;

            IL.Terraria.Player.CollideWithNPCs += Player_CollideWithNPCs;
            IL.Terraria.Player.DashMovement += Player_DashMovement;
            IL.Terraria.Player.JumpMovement += Player_JumpMovement;
            IL.Terraria.Player.Update += Player_Update;
            IL.Terraria.GameContent.Shaders.WaterShaderData.DrawWaves += WaterShaderData_DrawWaves;
            IL.Terraria.NPC.UpdateNPC_Inner += NPC_UpdateNPC_Inner;
            IL.Terraria.Item.GetPickedUpByMonsters += Item_GetPickedUpByMonsters;
            IL.Terraria.NPC.BeHurtByOtherNPC += NPC_BeHurtByOtherNPC;
            IL.Terraria.Player.Update_NPCCollision += Player_Update_NPCCollision;
        }

        //checks if the point is in the rectangle, including boundary points
        bool CheckAABBvPoint(Vector2 position, Vector2 dimensions, Vector2 point)
        {
            return point.X >= position.X && point.X <= (position.X + dimensions.X) && point.Y >= position.Y && point.Y <= (position.Y + dimensions.Y);
        }

        //A patch that fixes what I think is a bug in vanilla collision for things like zenith in which they don't collide when fully enclosed
        private bool Collision_CheckAABBvLineCollision_Vector2_Vector2_Vector2_Vector2_float_refSingle(On.Terraria.Collision.orig_CheckAABBvLineCollision_Vector2_Vector2_Vector2_Vector2_float_refSingle orig, Vector2 objectPosition, Vector2 objectDimensions, Vector2 lineStart, Vector2 lineEnd, float lineWidth, ref float collisionPoint)
        {
            if (orig(objectPosition, objectDimensions, lineStart, lineEnd, lineWidth, ref collisionPoint)) return true;
            else if (CheckAABBvPoint(objectPosition, objectDimensions, lineStart)) { collisionPoint = 0f; return true; }
            else return false;
        }

        private void NPC_UpdateCollision(On.Terraria.NPC.orig_UpdateCollision orig, NPC self)
        {
            MultiHitboxNPC multiHitbox;
            if (self.TryGetGlobalNPC<MultiHitboxNPC>(out multiHitbox))
            {
                if (multiHitbox.useMultipleHitboxes)
                {
                    multiHitbox.doDataReset = true;
                    Vector2 oldCenter = self.Center;
                    self.width = multiHitbox.widthForInteractions;
                    self.height = multiHitbox.heightForInteractions;
                    self.Center = oldCenter;
                }

                orig(self);

                if (multiHitbox.useMultipleHitboxes && multiHitbox.doDataReset)
                {
                    multiHitbox.doDataReset = false;
                    Vector2 oldCenter = self.Center;
                    self.width = multiHitbox.preModifyDataWidth;
                    self.height = multiHitbox.preModifyDataHeight;
                    self.Center = oldCenter;
                }
            }
            else
            {
                orig(self);
            }
        }

        private void NPC_UpdateNPC_BuffApplyVFX(On.Terraria.NPC.orig_UpdateNPC_BuffApplyVFX orig, NPC self)
        {
            orig(self);

            MultiHitboxNPC multiHitbox;
            if (self.TryGetGlobalNPC<MultiHitboxNPC>(out multiHitbox))
                if (multiHitbox.useMultipleHitboxes && multiHitbox.doDataReset)
                {
                    multiHitbox.doDataReset = false;
                    Vector2 oldCenter = self.Center;
                    self.width = multiHitbox.preModifyDataWidth;
                    self.height = multiHitbox.preModifyDataHeight;
                    self.Center = oldCenter;
                }
        }

        private void NPC_UpdateNPC_BuffSetFlags(On.Terraria.NPC.orig_UpdateNPC_BuffSetFlags orig, NPC self, bool lowerBuffTime)
        {
            MultiHitboxNPC multiHitbox;
            if (self.TryGetGlobalNPC<MultiHitboxNPC>(out multiHitbox))
                if (multiHitbox.useMultipleHitboxes)
                {
                    multiHitbox.doDataReset = true;
                    Vector2 oldCenter = self.Center;
                    self.width = multiHitbox.widthForInteractions;
                    self.height = multiHitbox.heightForInteractions;
                    self.Center = oldCenter;
                }

            orig(self, lowerBuffTime);
        }

        //modifies coin dust locations
        private void NPC_UpdateNPC_Inner(ILContext il)
        {
            ILCursor c = new ILCursor(il);

            if (!c.TryGotoNext(MoveType.After,
                i => i.MatchCall(typeof(Main).GetProperty("rand", BindingFlags.Static | BindingFlags.Public).GetGetMethod()),
                i => i.MatchLdloc(10),
                i => i.MatchConvI4(),
                i => i.MatchCallvirt(typeof(UnifiedRandom).GetMethod("Next", BindingFlags.Public | BindingFlags.Instance, new Type[] { typeof(int) })),
                i => i.MatchBrtrue(out _)
                ))
            {
                GetInstance<MultiHitboxNPCLibrary>().Logger.Debug("Failed to find patch location");
                return;
            }
            
            //first part of the patch
            c.Emit(OpCodes.Ldarg, 0);
            c.EmitDelegate<Action<NPC>>((npc) =>
            {
                MultiHitboxNPC multiHitbox;
                if (npc.TryGetGlobalNPC<MultiHitboxNPC>(out multiHitbox))
                    if (multiHitbox.useMultipleHitboxes)
                    {
                        multiHitbox.doDataReset = true;
                        multiHitbox.preModifyDataCenter = npc.Center;

                        RectangleHitbox newHitbox = multiHitbox.hitboxes.GetRandomHitbox(Main.rand);

                        npc.position = newHitbox.hitbox.TopLeft();
                        npc.width = newHitbox.hitbox.Width;
                        npc.height = newHitbox.hitbox.Height;
                    }
            });

            c.Index += 38;

            //second part of the patch
            c.Emit(OpCodes.Ldarg, 0);
            c.EmitDelegate<Action<NPC>>((npc) =>
            {
                MultiHitboxNPC multiHitbox;
                if (npc.TryGetGlobalNPC<MultiHitboxNPC>(out multiHitbox))
                    if (multiHitbox.useMultipleHitboxes && multiHitbox.doDataReset)
                    {
                        multiHitbox.doDataReset = false;
                        npc.width = multiHitbox.preModifyDataWidth;
                        npc.height = multiHitbox.preModifyDataHeight;
                        npc.Center = multiHitbox.preModifyDataCenter;
                    }
            });
        }

        //adjusts knockback for players
        private void Player_Update_NPCCollision(ILContext il)
        {
            ILCursor c = new ILCursor(il);

            if (!c.TryGotoNext(MoveType.After,
                i => i.MatchLdcI4(-1),
                i => i.MatchStloc(10),
                i => i.MatchLdsfld(typeof(Main).GetField("npc", BindingFlags.Public | BindingFlags.Static)),
                i => i.MatchLdloc(1),
                i => i.MatchLdelemRef(),
                i => i.MatchLdflda(typeof(Entity).GetField("position", BindingFlags.Public | BindingFlags.Instance)),
                i => i.MatchLdfld(typeof(Vector2).GetField("X", BindingFlags.Public | BindingFlags.Instance)),
                i => i.MatchLdsfld(typeof(Main).GetField("npc", BindingFlags.Public | BindingFlags.Static)),
                i => i.MatchLdloc(1),
                i => i.MatchLdelemRef(),
                i => i.MatchLdfld(typeof(Entity).GetField("width", BindingFlags.Public | BindingFlags.Instance)),
                i => i.MatchLdcI4(2),
                i => i.MatchDiv(),
                i => i.MatchConvR4(),
                i => i.MatchAdd()
                ))
            {
                GetInstance<MultiHitboxNPCLibrary>().Logger.Debug("Failed to find patch location");
                return;
            }

            //replaces the value with our better value if needed
            c.Emit(OpCodes.Ldsfld, typeof(Main).GetField("npc", BindingFlags.Public | BindingFlags.Static));
            c.Emit(OpCodes.Ldloc, 1);
            c.Emit(OpCodes.Ldelem_Ref);
            c.EmitDelegate<Func<float, NPC, float>>((defaultValue, npc) =>
            {
                if (npc.TryGetGlobalNPC<MultiHitboxNPC>(out MultiHitboxNPC multiHitbox) && multiHitbox.useMultipleHitboxes)
                {
                    return multiHitbox.mostRecentHitbox.hitbox.Center.X;
                }
                return defaultValue;
            });
        }

        //adjusts knockback for npcs
        private void NPC_BeHurtByOtherNPC(ILContext il)
        {
            ILCursor c = new ILCursor(il);

            if (!c.TryGotoNext(MoveType.After,
                i => i.MatchLdarg(2),
                i => i.MatchCallvirt(typeof(Entity).GetProperty("Center", BindingFlags.Public | BindingFlags.Instance).GetGetMethod()),
                i => i.MatchLdfld(typeof(Vector2).GetField("X", BindingFlags.Public | BindingFlags.Instance)),
                i => i.MatchLdarg(0),
                i => i.MatchCall(typeof(Entity).GetProperty("Center", BindingFlags.Public | BindingFlags.Instance).GetGetMethod()),
                i => i.MatchLdfld(typeof(Vector2).GetField("X", BindingFlags.Public | BindingFlags.Instance)),
                i => i.MatchBleUn(out _),
                i => i.MatchLdcI4(-1),
                i => i.MatchBr(out _),
                i => i.MatchLdcI4(1),
                i => i.MatchStloc(3)
                ))
            {
                GetInstance<MultiHitboxNPCLibrary>().Logger.Debug("Failed to find patch location");
                return;
            }

            c.Emit(OpCodes.Ldarg, 2);
            c.Emit(OpCodes.Ldarg, 0);
            c.Emit(OpCodes.Ldloc, 3);
            c.EmitDelegate<Func<NPC, NPC, int, int>>((targetNpc, npc, defaultValue) =>
            {
                MultiHitboxNPC multiHitbox;
                MultiHitboxNPC targetMultiHitbox;
                if (npc.TryGetGlobalNPC<MultiHitboxNPC>(out multiHitbox) && multiHitbox.useMultipleHitboxes)
                {
                    if (targetNpc.TryGetGlobalNPC<MultiHitboxNPC>(out targetMultiHitbox) && targetMultiHitbox.useMultipleHitboxes)
                    {
                        //both are multiHitboxNPCs
                        return ((!(targetMultiHitbox.mostRecentHitbox.hitbox.Center.X > multiHitbox.mostRecentHitbox.hitbox.Center.X)) ? 1 : (-1));
                    }
                    //npc is a multiHitboxNPC
                    return ((!(targetNpc.Center.X > multiHitbox.mostRecentHitbox.hitbox.Center.X)) ? 1 : (-1));
                }
                else if (targetNpc.TryGetGlobalNPC<MultiHitboxNPC>(out targetMultiHitbox) && targetMultiHitbox.useMultipleHitboxes)
                {
                    //target is a multiHitboxNPC
                    return ((!(targetMultiHitbox.mostRecentHitbox.hitbox.Center.X > npc.Center.X)) ? 1 : (-1));
                }
                return defaultValue;
            });
            c.Emit(OpCodes.Stloc, 3);
        }

        private void WaterShaderData_DrawWaves(ILContext il)
        {
            ILCursor c = new ILCursor(il);

            if (!c.TryGotoNext(MoveType.After,
                i => i.MatchLdsfld(typeof(Main).GetField("npc", BindingFlags.Static | BindingFlags.Public)),
                i => i.MatchLdloc(7),
                i => i.MatchLdelemRef(),
                i => i.MatchStloc(8)
                ))
            {
                GetInstance<MultiHitboxNPCLibrary>().Logger.Debug("Failed to find patch location");
                return;
            }

            c.Emit(OpCodes.Ldloc, 8);
            c.EmitDelegate<Action<NPC>>((npc) =>
            {
                MultiHitboxNPC multiHitbox;
                if (npc.TryGetGlobalNPC<MultiHitboxNPC>(out multiHitbox))
                    if (multiHitbox.useMultipleHitboxes)
                    {
                        multiHitbox.doDataReset = true;
                        Vector2 oldCenter = npc.Center;
                        npc.width = multiHitbox.widthForInteractions;
                        npc.height = multiHitbox.heightForInteractions;
                        npc.Center = oldCenter;
                    }
            });

            if (!c.TryGotoNext(MoveType.After,
                i => i.MatchLdloc(7),
                i => i.MatchLdcI4(1),
                i => i.MatchAdd(),
                i => i.MatchStloc(7),
                i => i.MatchLdloc(7),
                i => i.MatchLdcI4(200),
                i => i.MatchBlt(out _)
                ))
            {
                GetInstance<MultiHitboxNPCLibrary>().Logger.Debug("Failed to find patch location");
                return;
            }

            c.EmitDelegate<Action>(() =>
            {
                for (int i = 0; i < Main.maxNPCs; i++)
                {
                    NPC npc = Main.npc[i];
                    if (npc == null || !npc.active) continue;
                    MultiHitboxNPC multiHitbox;
                    if (npc.TryGetGlobalNPC<MultiHitboxNPC>(out multiHitbox))
                        if (multiHitbox.useMultipleHitboxes && multiHitbox.doDataReset)
                        {
                            multiHitbox.doDataReset = false;
                            Vector2 oldCenter = npc.Center;
                            npc.width = multiHitbox.preModifyDataWidth;
                            npc.height = multiHitbox.preModifyDataHeight;
                            npc.Center = oldCenter;
                        }
                }
            });
        }

        private double NPC_StrikeNPC(On.Terraria.NPC.orig_StrikeNPC orig, NPC self, int Damage, float knockBack, int hitDirection, bool crit, bool noEffect, bool fromNet)
        {
            MultiHitboxNPC multiHitbox;
            if (self.TryGetGlobalNPC<MultiHitboxNPC>(out multiHitbox))
            {
                if (multiHitbox.useMultipleHitboxes)
                {
                    multiHitbox.doDataReset = true;
                    multiHitbox.preModifyDataCenter = self.Center;

                    self.position = multiHitbox.mostRecentHitbox.hitbox.TopLeft();
                    self.width = multiHitbox.mostRecentHitbox.hitbox.Width;
                    self.height = multiHitbox.mostRecentHitbox.hitbox.Height;
                }

                double output = orig(self, Damage, knockBack, hitDirection, crit, noEffect, fromNet);

                if (multiHitbox.useMultipleHitboxes && multiHitbox.doDataReset)
                {
                    multiHitbox.doDataReset = false;
                    self.width = multiHitbox.preModifyDataWidth;
                    self.height = multiHitbox.preModifyDataHeight;
                    self.Center = multiHitbox.preModifyDataCenter;
                }

                return output;
            }
            else
            {
                return orig(self, Damage, knockBack, hitDirection, crit, noEffect, fromNet);
            }
        }

        //ramming mounts + lawnmower
        private void Player_CollideWithNPCs(ILContext il)
        {
            ILCursor c = new ILCursor(il);

            ILLabel label = null;

            if (!c.TryGotoNext(MoveType.After,
                i => i.MatchLdloc(2),
                i => i.MatchCallvirt(typeof(NPC).GetMethod("getRect", BindingFlags.Instance | BindingFlags.Public)),
                i => i.MatchStloc(3),
                i => i.MatchLdarga(1),
                i => i.MatchLdloc(3),
                i => i.MatchCall(typeof(Rectangle).GetMethod("Intersects", BindingFlags.Instance | BindingFlags.Public, new Type[] { typeof(Rectangle) })),
                i => i.MatchBrfalse(out label)
                ))
            {
                GetInstance<MultiHitboxNPCLibrary>().Logger.Debug("Failed to find patch location");
                return;
            }

            c.Emit(OpCodes.Ldarg, 1);
            c.Emit(OpCodes.Ldloc, 2);
            c.EmitDelegate<Func<Rectangle, NPC, bool>>((playerHitbox, npc) =>
            {
                MultiHitboxNPC multiHitbox;
                if (npc.TryGetGlobalNPC<MultiHitboxNPC>(out multiHitbox))
                    return !multiHitbox.useMultipleHitboxes || multiHitbox.CollideRectangle(npc, playerHitbox);
                return true;
            });
            c.Emit(OpCodes.Brfalse, label);
        }

        private void Item_GetPickedUpByMonsters(ILContext il)
        {
            ILCursor c = new ILCursor(il);

            ILLabel label = null;

            if (!c.TryGotoNext(MoveType.After,
                i => i.MatchLdloca(0),
                i => i.MatchLdloc(6),
                i => i.MatchCall(typeof(Rectangle).GetMethod("Intersects", BindingFlags.Instance | BindingFlags.Public, new Type[] { typeof(Rectangle) })),
                i => i.MatchBrfalse(out label)
                ))
            {
                GetInstance<MultiHitboxNPCLibrary>().Logger.Debug("Failed to find patch location");
                return;
            }

            c.Emit(OpCodes.Ldloc, 0);
            c.Emit(OpCodes.Ldsfld, typeof(Main).GetField("npc", BindingFlags.Static | BindingFlags.Public));
            c.Emit(OpCodes.Ldloc, 1);
            c.Emit(OpCodes.Ldelem_Ref);
            c.EmitDelegate<Func<Rectangle, NPC, bool>>((itemHitbox, npc) =>
            {
                MultiHitboxNPC multiHitbox;
                if (npc.TryGetGlobalNPC<MultiHitboxNPC>(out multiHitbox))
                    return !multiHitbox.useMultipleHitboxes || multiHitbox.CollideRectangle(npc, itemHitbox);
                return true;
            });
            c.Emit(OpCodes.Brfalse, label);
        }

        //minecart collision
        private void Player_Update(ILContext il)
        {
            ILCursor c = new ILCursor(il);

            ILLabel label = null;

            if (!c.TryGotoNext(MoveType.After,
                i => i.MatchConvI4(),
                i => i.MatchLdsfld(typeof(Main).GetField("npc", BindingFlags.Static | BindingFlags.Public)),
                i => i.MatchLdloc(145),
                i => i.MatchLdelemRef(),
                i => i.MatchLdfld(typeof(Entity).GetField("width", BindingFlags.Instance | BindingFlags.Public)),
                i => i.MatchLdsfld(typeof(Main).GetField("npc", BindingFlags.Static | BindingFlags.Public)),
                i => i.MatchLdloc(145),
                i => i.MatchLdelemRef(),
                i => i.MatchLdfld(typeof(Entity).GetField("height", BindingFlags.Instance | BindingFlags.Public)),
                i => i.MatchNewobj(typeof(Rectangle).GetConstructor(new Type[] { typeof(int), typeof(int), typeof(int), typeof(int) })),
                i => i.MatchCall(typeof(Rectangle).GetMethod("Intersects", BindingFlags.Instance | BindingFlags.Public, new Type[] { typeof(Rectangle) })),
                i => i.MatchBrfalse(out label)
                ))
            {
                GetInstance<MultiHitboxNPCLibrary>().Logger.Debug("Failed to find patch location");
                return;
            }

            c.Emit(OpCodes.Ldloc, 144);
            c.Emit(OpCodes.Ldsfld, typeof(Main).GetField("npc", BindingFlags.Static | BindingFlags.Public));
            c.Emit(OpCodes.Ldloc, 145);
            c.Emit(OpCodes.Ldelem_Ref);
            c.EmitDelegate<Func<Rectangle, NPC, bool>>((cartHitbox, npc) =>
            {
                MultiHitboxNPC multiHitbox;
                if (npc.TryGetGlobalNPC<MultiHitboxNPC>(out multiHitbox))
                    return !multiHitbox.useMultipleHitboxes || multiHitbox.CollideRectangle(npc, cartHitbox);
                return true;
            });
            c.Emit(OpCodes.Brfalse, label);
        }

        //lava collision with custom hitboxes
        private bool NPC_Collision_LavaCollision(On.Terraria.NPC.orig_Collision_LavaCollision orig, NPC self)
        {
            MultiHitboxNPC multiHitbox;
            if (self.TryGetGlobalNPC<MultiHitboxNPC>(out multiHitbox))
                if (multiHitbox.useMultipleHitboxes)
                {
                    multiHitbox.hitboxes.Colliding(self, (hitbox) => {
                        return Collision.LavaCollision(hitbox.TopLeft(), hitbox.Width, hitbox.Height);
                    });
                    return false;
                }
            return orig(self);
        }

        //slime/qs mount and golf cart apparently
        private void Player_JumpMovement(ILContext il)
        {
            ILCursor c = new ILCursor(il);

            ILLabel label = null;

            if (!c.TryGotoNext(MoveType.After,
                i => i.MatchLdloc(2),
                i => i.MatchCallvirt(typeof(NPC).GetMethod("getRect", BindingFlags.Instance | BindingFlags.Public)),
                i => i.MatchStloc(3),
                i => i.MatchLdloca(0),
                i => i.MatchLdloc(3),
                i => i.MatchCall(typeof(Rectangle).GetMethod("Intersects", BindingFlags.Instance | BindingFlags.Public, new Type[] { typeof(Rectangle) })),
                i => i.MatchBrfalse(out label)
                ))
            {
                GetInstance<MultiHitboxNPCLibrary>().Logger.Debug("Failed to find patch location");
                return;
            }

            c.Emit(OpCodes.Ldloc, 0);
            c.Emit(OpCodes.Ldloc, 2);
            c.EmitDelegate<Func<Rectangle, NPC, bool>>((jumpHitbox, npc) =>
            {
                MultiHitboxNPC multiHitbox;
                if (npc.TryGetGlobalNPC<MultiHitboxNPC>(out multiHitbox))
                    return !multiHitbox.useMultipleHitboxes || multiHitbox.CollideRectangle(npc, jumpHitbox);
                return true;
            });
            c.Emit(OpCodes.Brfalse, label);

            ILLabel label2 = null;

            if (!c.TryGotoNext(MoveType.After,
                i => i.MatchLdloc(10),
                i => i.MatchCallvirt(typeof(NPC).GetMethod("getRect", BindingFlags.Instance | BindingFlags.Public)),
                i => i.MatchStloc(11),
                i => i.MatchLdloca(8),
                i => i.MatchLdloc(11),
                i => i.MatchCall(typeof(Rectangle).GetMethod("Intersects", BindingFlags.Instance | BindingFlags.Public, new Type[] { typeof(Rectangle) })),
                i => i.MatchBrfalse(out label2)
                ))
            {
                GetInstance<MultiHitboxNPCLibrary>().Logger.Debug("Failed to find patch location");
                return;
            }

            c.Emit(OpCodes.Ldloc, 8);
            c.Emit(OpCodes.Ldloc, 10);
            c.EmitDelegate<Func<Rectangle, NPC, bool>>((jumpHitbox, npc) =>
            {
                MultiHitboxNPC multiHitbox;
                if (npc.TryGetGlobalNPC<MultiHitboxNPC>(out multiHitbox))
                    return !multiHitbox.useMultipleHitboxes || multiHitbox.CollideRectangle(npc, jumpHitbox);
                return true;
            });
            c.Emit(OpCodes.Brfalse, label2);
        }

        //make bonks respect custom hitboxes
        private void Player_DashMovement(ILContext il)
        {
            ILCursor c = new ILCursor(il);

            ILLabel label = null;

            if (!c.TryGotoNext(MoveType.After,
                i => i.MatchLdloc(2),
                i => i.MatchCallvirt(typeof(NPC).GetMethod("getRect", BindingFlags.Instance | BindingFlags.Public)),
                i => i.MatchStloc(3),
                i => i.MatchLdloca(0),
                i => i.MatchLdloc(3),
                i => i.MatchCall(typeof(Rectangle).GetMethod("Intersects", BindingFlags.Instance | BindingFlags.Public, new Type[] { typeof(Rectangle) })),
                i => i.MatchBrfalse(out label)
                ))
            {
                GetInstance<MultiHitboxNPCLibrary>().Logger.Debug("Failed to find patch location");
                return;
            }

            c.Emit(OpCodes.Ldloc, 0);
            c.Emit(OpCodes.Ldloc, 2);
            c.EmitDelegate<Func<Rectangle, NPC, bool>>((dashHitbox, npc) =>
            {
                MultiHitboxNPC multiHitbox;
                if (npc.TryGetGlobalNPC<MultiHitboxNPC>(out multiHitbox))
                    return !multiHitbox.useMultipleHitboxes || multiHitbox.CollideRectangle(npc, dashHitbox);
                return true;
            });
            c.Emit(OpCodes.Brfalse, label);

            ILLabel label2 = null;

            if (!c.TryGotoNext(MoveType.After,
                i => i.MatchLdloc(11),
                i => i.MatchCallvirt(typeof(NPC).GetMethod("getRect", BindingFlags.Instance | BindingFlags.Public)),
                i => i.MatchStloc(12),
                i => i.MatchLdloca(9),
                i => i.MatchLdloc(12),
                i => i.MatchCall(typeof(Rectangle).GetMethod("Intersects", BindingFlags.Instance | BindingFlags.Public, new Type[] { typeof(Rectangle) })),
                i => i.MatchBrfalse(out label2)
                ))
            {
                GetInstance<MultiHitboxNPCLibrary>().Logger.Debug("Failed to find patch location");
                return;
            }

            c.Emit(OpCodes.Ldloc, 9);
            c.Emit(OpCodes.Ldloc, 11);
            c.EmitDelegate<Func<Rectangle, NPC, bool>>((dashHitbox, npc) =>
            {
                MultiHitboxNPC multiHitbox;
                if (npc.TryGetGlobalNPC<MultiHitboxNPC>(out multiHitbox))
                    return !multiHitbox.useMultipleHitboxes || multiHitbox.CollideRectangle(npc, dashHitbox);
                return true;
            });
            c.Emit(OpCodes.Brfalse, label2);
        }

        static Rectangle rememberItemRectangle;
        private void Player_ItemCheck_MeleeHitNPCs(On.Terraria.Player.orig_ItemCheck_MeleeHitNPCs orig, Player self, Item sItem, Rectangle itemRectangle, int originalDamage, float knockBack)
        {
            rememberItemRectangle = itemRectangle;
            orig(self, sItem, itemRectangle, originalDamage, knockBack);
        }

        public bool CollideRectangle(NPC npc, Rectangle collider)
        {
            return hitboxes.Colliding(npc, (hitbox) =>
            {
                return collider.Intersects(hitbox);
            });
        }

        public override bool? CanBeHitByItem(NPC npc, Player player, Item item)
        {
            if (useMultipleHitboxes && !CollideRectangle(npc, rememberItemRectangle)) return false;
            return null;
        }

        public bool CollideProjectile(NPC npc, Projectile projectile, Rectangle projectileHitbox)
        {
            return hitboxes.Colliding(npc, (hitbox) =>
            {
                return projectile.Colliding(projectileHitbox, hitbox);
            });
        }

        public override bool? CanBeHitByProjectile(NPC npc, Projectile projectile)
        {
            if (useMultipleHitboxes && !CollideProjectile(npc, projectile, projectile.Hitbox)) return false;
            return null;
        }

        public override bool CanHitPlayer(NPC npc, Player target, ref int cooldownSlot)
        {
            if (useMultipleHitboxes && !CollideRectangle(npc, target.Hitbox)) return false;
            return true;
        }

        public override bool? CanHitNPC(NPC npc, NPC target)
        {
            MultiHitboxNPC targetMultiHitbox;
            if (!target.TryGetGlobalNPC<MultiHitboxNPC>(out targetMultiHitbox)) return null;
            if (useMultipleHitboxes)
            {
                if (targetMultiHitbox.useMultipleHitboxes)
                {
                    if (hitboxes.Colliding(npc, (hitbox) =>
                    {
                        return targetMultiHitbox.hitboxes.Colliding(target, (targetHitbox) =>
                        {
                            return hitbox.Intersects(targetHitbox);
                        });
                    }))
                    {
                        return null;
                    }
                    return false;
                }
                if (!CollideRectangle(npc, target.Hitbox)) return false;
                return null;
            }
            if (targetMultiHitbox.useMultipleHitboxes && !targetMultiHitbox.CollideRectangle(target, npc.Hitbox)) return false;
            return null;
        }

        public override void SetDefaults(NPC npc)
        {
            widthForInteractions = npc.width;
            heightForInteractions = npc.height;
        }

        public override bool? DrawHealthBar(NPC npc, byte hbPosition, ref float scale, ref Vector2 position)
        {
            if (useMultipleHitboxes)
            {
                position = new Vector2(npc.Center.X, npc.Center.Y - heightForInteractions / 2 + npc.gfxOffY);
                if (Main.HealthBarDrawSettings == 1)
                {
                    position.Y += (float)heightForInteractions + 10f + Main.NPCAddHeight(npc);
                }
                else if (Main.HealthBarDrawSettings == 2)
                {
                    position.Y -= 24f + Main.NPCAddHeight(npc) / 2f;
                }
                return true;
            }
            return null;
        }

        public override bool PreAI(NPC npc)
        {
            if (useMultipleHitboxes)
            {
                Vector2 oldCenter = npc.Center;
                npc.width = widthForInteractions;
                npc.height = heightForInteractions;
                npc.Center = oldCenter;
            }

            return true;
        }

        public override void PostAI(NPC npc)
        {
            if (useMultipleHitboxes)
            {
                //update width and height for special interactions before we reset the hitbox
                widthForInteractions = npc.width;
                heightForInteractions = npc.height;

                Point inflate = hitboxes.InflationFrom(npc.Center);

                npc.position = npc.Center - inflate.ToVector2();
                npc.width = 2 * inflate.X;
                npc.height = 2 * inflate.Y;

                //store hitbox data to be restored after special interactions
                preModifyDataWidth = npc.width;
                preModifyDataHeight = npc.height;
            }
        }

        public void AssignHitboxFrom(List<Rectangle> rectangles, MultiHitboxAssignmentMode assignmentMode = MultiHitboxAssignmentMode.Nested)
        {
            if (rectangles == null)
            {
                hitboxes = null;
                return;
            }

            if (hitboxes == null || hitboxes.HitboxCount != rectangles.Count)
            {
                hitboxes = MultiHitbox.CreateFrom(rectangles, assignmentMode);
            }
            else
            {
                for (int i = 0; i < Math.Min(hitboxes.HitboxCount, rectangles.Count); i++)
                {
                    RectangleHitbox rectangleHitbox = hitboxes.GetHitbox(i);
                    rectangleHitbox.hitbox = rectangles[i];
                }
            }
        }

        //hitbox-drawing method for debugging
        public override void PostDraw(NPC npc, SpriteBatch spriteBatch, Vector2 screenPos, Color drawColor)
        {
            if (useMultipleHitboxes && doDebugDraws)
            {
                hitboxes.Draw(spriteBatch, screenPos, Color.Red * 0.5f);
            }
        }
    }
}

