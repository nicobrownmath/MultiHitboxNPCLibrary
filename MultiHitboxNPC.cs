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
    //TODO: Produce death sound from (real center? the closest center to the player?)
    public class MultiHitboxNPC : GlobalNPC
    {
        public override bool InstancePerEntity => true;

        public static HashSet<int> MultiHitboxNPCTypes;

        public override bool AppliesToEntity(NPC entity, bool lateInstantiation)
        {
            return MultiHitboxNPCTypes.Contains(entity.type);
        }

        //why is this not a thing normally
        public NPC NPC;

        public bool useMultipleHitboxes => hitboxes != null;
        public ANPCHitbox hitboxes;
        public RectangleHitbox mostRecentHitbox;

        public int widthForInteractions;
        public int heightForInteractions;

        public Vector2 preModifyDataCenter { get; private set; }
        public int preModifyDataWidth { get; private set; }
        public int preModifyDataHeight { get; private set; }
        bool doDataReset;

        public bool doDebugDraws;

        #region Loading & Patches
        public override void Load()
        {
            Terraria.On_Collision.CheckAABBvLineCollision_Vector2_Vector2_Vector2_Vector2_float_refSingle += Collision_CheckAABBvLineCollision_Vector2_Vector2_Vector2_Vector2_float_refSingle;

            Terraria.On_Player.ItemCheck_MeleeHitNPCs += Player_ItemCheck_MeleeHitNPCs;
            Terraria.On_NPC.Collision_LavaCollision += NPC_Collision_LavaCollision;
            Terraria.On_NPC.UpdateNPC_BuffSetFlags += NPC_UpdateNPC_BuffSetFlags;
            Terraria.On_NPC.UpdateNPC_BuffApplyVFX += NPC_UpdateNPC_BuffApplyVFX;
            Terraria.On_NPC.UpdateCollision += NPC_UpdateCollision;
            // TODO: Fix this guy !
            //Terraria.On_NPC.StrikeNPC += NPC_StrikeNPC;
            Terraria.On_Projectile.Update += Projectile_Update;
            Terraria.On_NPC.CanBeChasedBy += NPC_CanBeChasedBy;

            //Terraria.IL_Player.CollideWithNPCs += Player_CollideWithNPCs;
            Terraria.IL_Player.DashMovement += Player_DashMovement;
            Terraria.IL_Player.JumpMovement += Player_JumpMovement;
            Terraria.IL_Player.Update += Player_Update;
            Terraria.GameContent.Shaders.IL_WaterShaderData.DrawWaves += WaterShaderData_DrawWaves;
            Terraria.IL_NPC.UpdateNPC_Inner += NPC_UpdateNPC_Inner;
            Terraria.IL_Item.GetPickedUpByMonsters_Money += Item_GetPickedUpByMonsters;
            Terraria.IL_NPC.BeHurtByOtherNPC += NPC_BeHurtByOtherNPC;
            Terraria.IL_Player.Update_NPCCollision += Player_Update_NPCCollision;
            Terraria.IL_Main.DrawMouseOver += Main_DrawMouseOver;
            Terraria.IL_Player.MinionNPCTargetAim += Player_MinionNPCTargetAim1;

            MultiHitboxNPCTypes = new HashSet<int>();
        }

        public override void Unload()
        {
            MultiHitboxNPCTypes = null;
        }

        //modify minion targeting
        private void Player_MinionNPCTargetAim1(ILContext il)
        {
            ILCursor c = new ILCursor(il);

            if (!c.TryGotoNext(MoveType.After,
                i => i.MatchLdsfld(typeof(Main).GetField("npc", BindingFlags.Static | BindingFlags.Public)),
                i => i.MatchLdloc(1),
                i => i.MatchLdelemRef(),
                i => i.MatchCallvirt(typeof(Entity).GetProperty("Hitbox", BindingFlags.Instance | BindingFlags.Public).GetGetMethod()),
                i => i.MatchLdloc(0),
                i => i.MatchCall(typeof(Utils).GetMethod("Distance", BindingFlags.Static | BindingFlags.Public, new Type[] { typeof(Rectangle), typeof(Vector2) }))
                ))
            {
                GetInstance<MultiHitboxNPCLibrary>().Logger.Debug("Failed to find patch location");
                return;
            }

            c.Emit(OpCodes.Ldloc, 1);
            c.EmitDelegate<Func<float, int, float>>((defaultDistance, npcIndex) =>
            {
                NPC npc = Main.npc[npcIndex];
                if (npc.TryGetGlobalNPC<MultiHitboxNPC>(out MultiHitboxNPC multiHitbox) && multiHitbox.useMultipleHitboxes)
                {
                    return multiHitbox.hitboxes.GetClosestHitbox(Main.MouseWorld, (hitbox) => hitbox.canBeDamaged).BoundingHitbox.Distance(Main.MouseWorld);
                }
                return defaultDistance;
            });
        }

        //cannot chase NPCs which have no damageable hitboxes
        private bool NPC_CanBeChasedBy(Terraria.On_NPC.orig_CanBeChasedBy orig, NPC self, object attacker, bool ignoreDontTakeDamage)
        {
            if (!orig(self, attacker, ignoreDontTakeDamage))
            {
                return false;
            }
            else
            {
                if (ignoreDontTakeDamage) return true;

                if (self.TryGetGlobalNPC<MultiHitboxNPC>(out MultiHitboxNPC multiHitbox))
                {
                    if (multiHitbox.useMultipleHitboxes) return multiHitbox.hitboxes.canBeDamaged;
                }
                return true;
            }
        }

        //homing projectiles home in on the closest segment
        private void Projectile_Update(Terraria.On_Projectile.orig_Update orig, Projectile self, int i)
        {
            //adjust to center at the closest segment
            if (self.TryGetGlobalProjectile<MultiHitboxNPCLibraryProjectile>(out MultiHitboxNPCLibraryProjectile mhProjectile) && mhProjectile.javelinSticking && mhProjectile.stuckToNPC != -1)
            {
                //special case for javelins
                NPC npc = Main.npc[mhProjectile.stuckToNPC];
                if (npc.TryGetGlobalNPC<MultiHitboxNPC>(out MultiHitboxNPC multiHitbox))
                {
                    if (multiHitbox.useMultipleHitboxes)
                    {
                        RectangleHitbox attachHitbox = multiHitbox.hitboxes.GetHitbox(mhProjectile.stuckToHitboxIndex);

                        multiHitbox.doDataReset = true;
                        multiHitbox.preModifyDataWidth = npc.width;
                        multiHitbox.preModifyDataHeight = npc.height;
                        multiHitbox.preModifyDataCenter = npc.Center;

                        Vector2 center = attachHitbox.BoundingHitbox.Center.ToVector2();
                        npc.width = (int)Math.Ceiling(multiHitbox.preModifyDataWidth + Math.Abs(multiHitbox.preModifyDataCenter.X - center.X) * 2);
                        npc.height = (int)Math.Ceiling(multiHitbox.preModifyDataHeight + Math.Abs(multiHitbox.preModifyDataCenter.Y - center.Y) * 2);
                        npc.Center = center;
                    }
                }
            }
            else
            {
                for (int j = 0; j < Main.maxNPCs; j++)
                {
                    NPC npc = Main.npc[j];
                    if (npc.active && npc.CanBeChasedBy(self))
                    {
                        if (npc.TryGetGlobalNPC<MultiHitboxNPC>(out MultiHitboxNPC multiHitbox))
                        {
                            if (multiHitbox.useMultipleHitboxes)
                            {
                                multiHitbox.doDataReset = true;
                                multiHitbox.preModifyDataWidth = npc.width;
                                multiHitbox.preModifyDataHeight = npc.height;
                                multiHitbox.preModifyDataCenter = npc.Center;

                                Vector2 center = multiHitbox.hitboxes.GetClosestHitbox(self.Center, (hitbox) => hitbox.canBeDamaged).BoundingHitbox.Center.ToVector2();
                                npc.width = (int)Math.Ceiling(multiHitbox.preModifyDataWidth + Math.Abs(multiHitbox.preModifyDataCenter.X - center.X) * 2);
                                npc.height = (int)Math.Ceiling(multiHitbox.preModifyDataHeight + Math.Abs(multiHitbox.preModifyDataCenter.Y - center.Y) * 2);
                                npc.Center = center;
                            }
                        }
                    }
                }
            }

            orig(self, i);

            for (int j = 0; j < Main.maxNPCs; j++)
            {
                NPC npc = Main.npc[j];
                if (npc.active)
                {
                    if (npc.TryGetGlobalNPC<MultiHitboxNPC>(out MultiHitboxNPC multiHitbox))
                    {
                        if (multiHitbox.useMultipleHitboxes && multiHitbox.doDataReset)
                        {
                            multiHitbox.doDataReset = false;
                            npc.width = multiHitbox.preModifyDataWidth;
                            npc.height = multiHitbox.preModifyDataHeight;
                            npc.Center = multiHitbox.preModifyDataCenter;
                        }
                    }
                }
            }
        }

        //adjusts mouseover text
        private void Main_DrawMouseOver(ILContext il)
        {
            ILCursor c = new ILCursor(il);

            if (!c.TryGotoNext(MoveType.After,
                i => i.MatchLdloca(0),
                i => i.MatchLdloc(12),
                i => i.MatchCall(typeof(Rectangle).GetMethod("Intersects", BindingFlags.Instance | BindingFlags.Public, new Type[] { typeof(Rectangle) })),
                i => i.MatchStloc(13)
                ))
            {
                GetInstance<MultiHitboxNPCLibrary>().Logger.Debug("Failed to find patch location");
                return;
            }

            c.Emit(OpCodes.Ldloc, 13);
            c.Emit(OpCodes.Ldloc, 0);
            c.Emit(OpCodes.Ldsfld, typeof(Main).GetField("npc", BindingFlags.Static | BindingFlags.Public));
            c.Emit(OpCodes.Ldloc, 10);
            c.Emit(OpCodes.Ldelem_Ref);
            c.EmitDelegate<Func<bool, Rectangle, NPC, bool>>((defaultValue, mouseRect, npc) =>
            {
                if (npc.TryGetGlobalNPC<MultiHitboxNPC>(out MultiHitboxNPC multiHitbox) && multiHitbox.useMultipleHitboxes)
                    return multiHitbox.CollideRectangle(npc, mouseRect);
                return defaultValue;
            });
            c.Emit(OpCodes.Stloc, 13);
        }

        //checks if the point is in the rectangle, including boundary points
        bool CheckAABBvPoint(Vector2 position, Vector2 dimensions, Vector2 point)
        {
            return point.X >= position.X && point.X <= (position.X + dimensions.X) && point.Y >= position.Y && point.Y <= (position.Y + dimensions.Y);
        }

        //A patch that fixes what I think is a bug in vanilla collision for things like zenith in which they don't collide when fully enclosed
        private bool Collision_CheckAABBvLineCollision_Vector2_Vector2_Vector2_Vector2_float_refSingle(Terraria.On_Collision.orig_CheckAABBvLineCollision_Vector2_Vector2_Vector2_Vector2_float_refSingle orig, Vector2 objectPosition, Vector2 objectDimensions, Vector2 lineStart, Vector2 lineEnd, float lineWidth, ref float collisionPoint)
        {
            if (orig(objectPosition, objectDimensions, lineStart, lineEnd, lineWidth, ref collisionPoint)) return true;
            else if (CheckAABBvPoint(objectPosition, objectDimensions, lineStart)) { collisionPoint = 0f; return true; }
            else return false;
        }

        //adjusts tile collision to only use the default hitbox
        private void NPC_UpdateCollision(Terraria.On_NPC.orig_UpdateCollision orig, NPC self)
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

        //adjusts buff effects to show up from the main npc
        private void NPC_UpdateNPC_BuffApplyVFX(Terraria.On_NPC.orig_UpdateNPC_BuffApplyVFX orig, NPC self)
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
        private void NPC_UpdateNPC_BuffSetFlags(Terraria.On_NPC.orig_UpdateNPC_BuffSetFlags orig, NPC self, bool lowerBuffTime)
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

        //adjusts coin pickup availability
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

        //adjusts wave drawing
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

        //adjusts on hit effects (damage numbers and hit sounds)
        private double NPC_StrikeNPC(Terraria.On_NPC.orig_StrikeNPC orig, NPC self, int Damage, float knockBack, int hitDirection, bool crit, bool noEffect, bool fromNet)
        {
            MultiHitboxNPC multiHitbox;
            if (self.TryGetGlobalNPC<MultiHitboxNPC>(out multiHitbox))
            {
                if (multiHitbox.useMultipleHitboxes)
                {
                    multiHitbox.doDataReset = true;

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
                    return !multiHitbox.useMultipleHitboxes || multiHitbox.CollideRectangle(npc, playerHitbox, needCanDamage: true);
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
                    return !multiHitbox.useMultipleHitboxes || multiHitbox.CollideRectangle(npc, cartHitbox, needCanBeDamaged: true);
                return true;
            });
            c.Emit(OpCodes.Brfalse, label);
        }

        //lava collision with custom hitboxes
        private bool NPC_Collision_LavaCollision(Terraria.On_NPC.orig_Collision_LavaCollision orig, NPC self)
        {
            MultiHitboxNPC multiHitbox;
            if (self.TryGetGlobalNPC<MultiHitboxNPC>(out multiHitbox))
                if (multiHitbox.useMultipleHitboxes)
                {
                    multiHitbox.hitboxes.Colliding(self, (hitbox) => {
                        return hitbox.canBeDamaged && Collision.LavaCollision(hitbox.BoundingHitbox.TopLeft(), hitbox.BoundingHitbox.Width, hitbox.BoundingHitbox.Height);
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
                    return !multiHitbox.useMultipleHitboxes || multiHitbox.CollideRectangle(npc, jumpHitbox, needCanBeDamaged: true);
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
                    return !multiHitbox.useMultipleHitboxes || multiHitbox.CollideRectangle(npc, jumpHitbox, needCanBeDamaged: true);
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
                    return !multiHitbox.useMultipleHitboxes || multiHitbox.CollideRectangle(npc, dashHitbox, needCanBeDamaged: true);
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
                    return !multiHitbox.useMultipleHitboxes || multiHitbox.CollideRectangle(npc, dashHitbox, needCanBeDamaged: true);
                return true;
            });
            c.Emit(OpCodes.Brfalse, label2);
        }

        //remember our item hitbox for canbehitbyitem checs
        static Rectangle rememberItemRectangle;
        private void Player_ItemCheck_MeleeHitNPCs(Terraria.On_Player.orig_ItemCheck_MeleeHitNPCs orig, Player self, Item sItem, Rectangle itemRectangle, int originalDamage, float knockBack)
        {
            rememberItemRectangle = itemRectangle;
            orig(self, sItem, itemRectangle, originalDamage, knockBack);
        }
        #endregion

        public bool CollideRectangle(NPC npc, Rectangle collider, bool needCanDamage = false, bool needCanBeDamaged = false)
        {
            return hitboxes.Colliding(npc, (hitbox) =>
            {
                return (!needCanBeDamaged || hitbox.canBeDamaged) && (!needCanDamage || hitbox.canDamage) && collider.Intersects(hitbox.BoundingHitbox);
            });
        }

        public static bool CollideWithRectangle(NPC npc, Rectangle collider, bool needCanDamage = false, bool needCanBeDamaged = false)
        {
            MultiHitboxNPC multiHitbox;
            if (npc.TryGetGlobalNPC<MultiHitboxNPC>(out multiHitbox))
                return multiHitbox.useMultipleHitboxes ? multiHitbox.CollideRectangle(npc, collider, needCanDamage, needCanBeDamaged) : npc.Hitbox.Intersects(collider);
            return npc.Hitbox.Intersects(collider);
        }

        public override bool? CanBeHitByItem(NPC npc, Player player, Item item)
        {
            if (useMultipleHitboxes && !CollideRectangle(npc, rememberItemRectangle, needCanBeDamaged: true)) return false;
            return null;
        }

        public bool CollideProjectile(NPC npc, Projectile projectile, Rectangle projectileHitbox)
        {
            if (!projectile.GetGlobalProjectile<MultiHitboxNPCLibraryProjectile>().badCollision)
            {
                return hitboxes.Colliding(npc, (hitbox) =>
                {
                    return hitbox.canBeDamaged && projectile.Colliding(projectileHitbox, hitbox.BoundingHitbox);
                });
            }
            else
            {
                ICollection<RectangleHitbox> allHitboxes = hitboxes.AllHitboxes();
                foreach (RectangleHitbox hitbox in allHitboxes)
                {
                    if (hitbox.canBeDamaged && projectile.Colliding(projectileHitbox, hitbox.BoundingHitbox))
                    {
                        mostRecentHitbox = hitbox;
                        return true;
                    }
                }
                return false;
            }
        }

        public static bool CollideWithProjectile(NPC npc, Projectile projectile, Rectangle projectileHitbox)
        {
            MultiHitboxNPC multiHitbox;
            if (npc.TryGetGlobalNPC<MultiHitboxNPC>(out multiHitbox))
                return multiHitbox.useMultipleHitboxes ? multiHitbox.CollideProjectile(npc, projectile, projectileHitbox) : npc.Hitbox.Intersects(projectileHitbox);
            return npc.Hitbox.Intersects(projectileHitbox);
        }

        public override bool? CanBeHitByProjectile(NPC npc, Projectile projectile)
        {
            if (useMultipleHitboxes && !CollideProjectile(npc, projectile, projectile.Hitbox)) return false;
            return null;
        }

        public override bool CanHitPlayer(NPC npc, Player target, ref int cooldownSlot)
        {
            if (useMultipleHitboxes && !CollideRectangle(npc, target.Hitbox, needCanDamage: true)) return false;
            return true;
        }

        public override bool CanHitNPC(NPC npc, NPC target)/* tModPorter Suggestion: Return true instead of null */
        {
            MultiHitboxNPC targetMultiHitbox;
            if (!target.TryGetGlobalNPC<MultiHitboxNPC>(out targetMultiHitbox)) return true;
            if (useMultipleHitboxes)
            {
                if (targetMultiHitbox.useMultipleHitboxes)
                {
                    if (hitboxes.Colliding(npc, (hitbox) =>
                    {
                        return hitbox.canDamage && targetMultiHitbox.CollideRectangle(target, hitbox.BoundingHitbox, needCanBeDamaged: true);
                    }))
                    {
                        return true;
                    }
                    return false;
                }
                if (!CollideRectangle(npc, target.Hitbox, needCanDamage: true)) return false;
                return true;
            }
            if (targetMultiHitbox.useMultipleHitboxes && !targetMultiHitbox.CollideRectangle(target, npc.Hitbox, needCanBeDamaged: true)) return false;
            return true;
        }

        public override void SetDefaults(NPC npc)
        {
            NPC = npc;

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
                preModifyDataCenter = npc.Center;
                doDataReset = false;
            }
        }

        public void AssignHitboxFrom(List<RectangleHitboxData> hitboxDatas, MultiHitboxAssignmentMode assignmentMode = MultiHitboxAssignmentMode.Nested)
        {
            if (hitboxDatas == null)
            {
                hitboxes = null;
                return;
            }

            if (hitboxes == null || hitboxes.HitboxCount != hitboxDatas.Count)
            {
                hitboxes = MultiHitbox.CreateFrom(hitboxDatas, assignmentMode);
            }
            else
            {
                for (int i = 0; i < Math.Min(hitboxes.HitboxCount, hitboxDatas.Count); i++)
                {
                    RectangleHitbox rectangleHitbox = hitboxes.GetHitbox(i);
                    rectangleHitbox.hitbox = hitboxDatas[i].Hitbox ?? rectangleHitbox.hitbox;
                    rectangleHitbox.canDamage = hitboxDatas[i].CanDamage ?? rectangleHitbox.canDamage;
                    rectangleHitbox.canBeDamaged = hitboxDatas[i].CanBeDamaged ?? rectangleHitbox.canBeDamaged;
                }
            }
            hitboxes.Refresh();

            //sync hitbox in MP
            if (Main.netMode == NetmodeID.Server)
            {
                ModPacket packet = Mod.GetPacket();
                packet.Write("MultiHitboxNPCLibrary:SyncNPC");
                packet.Write(NPC.whoAmI);
                hitboxes.Write(packet);
                packet.Send();
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