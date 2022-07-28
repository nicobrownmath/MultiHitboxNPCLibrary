using System;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace MultiHitboxNPCLibrary
{
    public class MultiHitboxNPCLibraryProjectile : GlobalProjectile
    {
        public bool badCollision;

        public override void SetDefaults(Projectile projectile)
        {
            switch(projectile.type)
            {
                case ProjectileID.BoneJavelin:
                case ProjectileID.Daybreak:
                    badCollision = true;
                    break;
            }
        }
    }
}

