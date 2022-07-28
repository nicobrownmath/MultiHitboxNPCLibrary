using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace MultiHitboxNPCLibrary
{
    public class MultiHitboxNPCLibraryProjectile : GlobalProjectile
    {
        public override bool InstancePerEntity => true;

        public bool badCollision;

        public bool javelinSticking;
        public int stuckToNPC = -1;
        public int stuckToHitboxIndex;

        public override void SetDefaults(Projectile projectile)
        {
            switch(projectile.type)
            {
                case ProjectileID.BoneJavelin:
                case ProjectileID.Daybreak:
                    javelinSticking = true;
                    badCollision = true;
                    break;
            }
        }

        public override void OnHitNPC(Projectile projectile, NPC target, int damage, float knockback, bool crit)
        {
            if (target.GetGlobalNPC<MultiHitboxNPC>().useMultipleHitboxes)
            {
                stuckToNPC = target.whoAmI;
                stuckToHitboxIndex = target.GetGlobalNPC<MultiHitboxNPC>().mostRecentHitbox.index;
            }
        }
    }
}

