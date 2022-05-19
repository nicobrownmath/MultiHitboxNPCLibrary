using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.ModLoader;
using Terraria.Utilities;

namespace MultiHitboxNPCLibrary
{
	public abstract class ANPCHitbox : ILoadable
    {
        public static Dictionary<string, ANPCHitbox> fromName = new Dictionary<string, ANPCHitbox>();
        public void Load(Mod mod)
        {
            fromName.Add(GetType().FullName, this);
        }

        public void Unload()
        {
            fromName = null;
        }


        public abstract bool Colliding(NPC npc, Func<ANPCHitbox, bool> collisionCheck);

        public virtual int HitboxCount { get; } //counts the total number of AABB hitboxes

        public Rectangle BoundingHitbox;

        public bool canDamage;
        public bool canBeDamaged;

        public Point InflationFrom(Vector2 center)
        {
            return new Point((int)Math.Max(center.X - BoundingHitbox.Left, BoundingHitbox.Right - center.X), (int)Math.Max(center.Y - BoundingHitbox.Top, BoundingHitbox.Bottom - center.Y));
        }

        public abstract ICollection<RectangleHitbox> AllHitboxes(); //returns a collection of all low-level hitboxes for the purposes of random segment drops

        //draw method is meant for debugging
        public virtual void Draw(SpriteBatch spriteBatch, Vector2 screenPos, Color drawColor)
        {
            spriteBatch.Draw(TextureAssets.MagicPixel.Value, new Rectangle(BoundingHitbox.X - (int)screenPos.X, BoundingHitbox.Y - (int)screenPos.Y, BoundingHitbox.Width, BoundingHitbox.Height), drawColor);
        }

        public abstract RectangleHitbox GetHitbox(int index);

        //for use with coins
        public virtual RectangleHitbox GetRandomHitbox(UnifiedRandom random)
        {
            return GetHitbox(random.Next(HitboxCount));
        }

        public abstract void Refresh();

        public void Write(ModPacket packet)
        {
            packet.Write(GetType().FullName);
            WriteTo(packet);
        }
        public virtual void WriteTo(ModPacket packet)
        {
            throw new NotImplementedException();
        }
        public static ANPCHitbox Read(BinaryReader reader)
        {
            return fromName[reader.ReadString()].ReadFrom(reader);
        }
        public virtual ANPCHitbox ReadFrom(BinaryReader reader)
        {
            throw new NotImplementedException();
        }
    }

    //a basic rectangle hitbox, used as a component of all other hitboxes
    public class RectangleHitbox : ANPCHitbox
    {
        public Rectangle hitbox; //the hitbox rectangle
        public int index; //our index in the overall multiHitbox

        public override int HitboxCount => 1;

        public RectangleHitbox(Rectangle hitbox, bool canDamage, bool canBeDamaged, int index)
        {
            this.hitbox = hitbox;
            this.canDamage = canDamage;
            this.canBeDamaged = canBeDamaged;
            this.index = index;
        }

        public override void Refresh()
        {
            BoundingHitbox = hitbox;
        }

        public override bool Colliding(NPC npc, Func<ANPCHitbox, bool> collisionCheck)
        {
            if (collisionCheck.Invoke(this))
            {
                npc.GetGlobalNPC<MultiHitboxNPC>().mostRecentHitbox = this;

                if(npc.ModNPC != null && npc.ModNPC is IMultiHitboxSegmentUpdate multiHitboxSegmentUpdate)
                {
                    multiHitboxSegmentUpdate.MultiHitboxSegmentUpdate(npc, this);
                }
                foreach(Instanced<GlobalNPC> global in npc.Globals)
                {
                    if (global.Instance is IMultiHitboxSegmentUpdate globalMultiHitboxSegmentUpdate)
                    {
                        globalMultiHitboxSegmentUpdate.MultiHitboxSegmentUpdate(npc, this);
                    }
                }
                return true;
            }
            return false;
        }

        public override ICollection<RectangleHitbox> AllHitboxes()
        {
            return new HashSet<RectangleHitbox>() { this };
        }

        public override RectangleHitbox GetHitbox(int index)
        {
            return this;
        }

        public override void WriteTo(ModPacket packet)
        {
            packet.Write(hitbox.X);
            packet.Write(hitbox.Y);
            packet.Write(hitbox.Width);
            packet.Write(hitbox.Height);
            packet.Write(canDamage);
            packet.Write(canBeDamaged);
            packet.Write(index);
        }

        public override ANPCHitbox ReadFrom(BinaryReader reader)
        {
            return new RectangleHitbox(new Rectangle(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()), reader.ReadBoolean(), reader.ReadBoolean(), reader.ReadInt32());
        }
    }

    public interface IMultiHitboxSegmentUpdate
    {
        public void MultiHitboxSegmentUpdate(NPC npc, RectangleHitbox mostRecentHitbox);
    }

    public struct RectangleHitboxData
    {
        public Rectangle? Hitbox { get; private set; }
        public bool? CanDamage { get; private set; }
        public bool? CanBeDamaged { get; private set; }

        public RectangleHitboxData(Rectangle? hitbox = null, bool? canDamage = null, bool? canBeDamaged = null)
        {
            Hitbox = hitbox;
            CanDamage = canDamage;
            CanBeDamaged = canBeDamaged;
        }
    }

    public enum MultiHitboxAssignmentMode
    {
        Basic,
        Nested
    }

    //a container for multiple hitboxes
    //does collision stuff automatically
    public class MultiHitbox : ANPCHitbox
    {
        public List<ANPCHitbox> hitboxes;
        int _hitboxCount;
        public override int HitboxCount => _hitboxCount;

        public MultiHitbox(List<ANPCHitbox> hitboxes)
        {
            this.hitboxes = hitboxes;
        }

        public static ANPCHitbox CreateFrom(List<RectangleHitboxData> hitboxDatas, MultiHitboxAssignmentMode assignmentMode = MultiHitboxAssignmentMode.Nested)
        {
            switch (assignmentMode)
            {
                case MultiHitboxAssignmentMode.Basic:
                    {
                        List<ANPCHitbox> hitboxes = new List<ANPCHitbox>();
                        int index = 0;
                        foreach (RectangleHitboxData hitboxData in hitboxDatas)
                        {
                            //these damage things and can be hit by default
                            hitboxes.Add(new RectangleHitbox(hitboxData.Hitbox ?? default(Rectangle), hitboxData.CanDamage ?? true, hitboxData.CanBeDamaged ?? true, index));
                            index++;
                        }
                        return new MultiHitbox(hitboxes);
                    }
                case MultiHitboxAssignmentMode.Nested:
                    {
                        int index = 0;
                        return NestedHitboxFrom(hitboxDatas, hitboxDatas.Count, ref index);
                    }
            }
            return null; //how did we get here
        }

        static ANPCHitbox NestedHitboxFrom(List<RectangleHitboxData> hitboxDatas, int maxDepthToReach, ref int index)
        {
            if (maxDepthToReach == 0)
            {
                throw new ArgumentException("MultiHitbox: hitboxes cannot be empty");
            }

            if (maxDepthToReach == 1)
            {
                RectangleHitbox output = new RectangleHitbox(hitboxDatas[index].Hitbox ?? default(Rectangle), hitboxDatas[index].CanDamage ?? true, hitboxDatas[index].CanBeDamaged ?? true, index);
                index++;
                return output;
            }

            List<ANPCHitbox> hitboxes = new List<ANPCHitbox>();
            for (int i = 0; i < 2; i++)
            {
                if (index < hitboxDatas.Count)
                    hitboxes.Add(NestedHitboxFrom(hitboxDatas, (maxDepthToReach + 1) / 2, ref index));
            }
            return new MultiHitbox(hitboxes);
        }

        public override void Refresh()
        {
            canDamage = false;
            canBeDamaged = false;
            _hitboxCount = 0;

            float left = float.MaxValue;
            float right = float.MinValue;
            float top = float.MaxValue;
            float bottom = float.MinValue;
            foreach (ANPCHitbox subHitbox in hitboxes)
            {
                subHitbox.Refresh();

                _hitboxCount += subHitbox.HitboxCount;

                if (subHitbox.canDamage) canDamage = true;
                if (subHitbox.canBeDamaged) canBeDamaged = true;

                Rectangle subBoundingBox = subHitbox.BoundingHitbox;
                if (subBoundingBox.Left < left) left = subBoundingBox.Left;
                if (subBoundingBox.Right > right) right = subBoundingBox.Right;
                if (subBoundingBox.Top < top) top = subBoundingBox.Top;
                if (subBoundingBox.Bottom > bottom) bottom = subBoundingBox.Bottom;
            }

            BoundingHitbox = new Rectangle((int)Math.Floor(left), (int)Math.Floor(top), (int)Math.Ceiling(right) - (int)Math.Floor(left), (int)Math.Ceiling(bottom) - (int)Math.Floor(top));
        }

        public override bool Colliding(NPC npc, Func<ANPCHitbox, bool> collisionCheck)
        {
            if (!collisionCheck.Invoke(this)) return false;
            foreach (ANPCHitbox subHitbox in hitboxes)
            {
                if (subHitbox.Colliding(npc, collisionCheck)) return true;
            }
            return false;
        }

        public override ICollection<RectangleHitbox> AllHitboxes()
        {
            HashSet<RectangleHitbox> output = new HashSet<RectangleHitbox>();
            foreach (ANPCHitbox subHitbox in hitboxes)
            {
                output.UnionWith(subHitbox.AllHitboxes());
            }
            return output;
        }

        public override void Draw(SpriteBatch spriteBatch, Vector2 screenPos, Color drawColor)
        {
            foreach (ANPCHitbox subHitbox in hitboxes)
            {
                subHitbox.Draw(spriteBatch, screenPos, drawColor);
            }
        }

        public override RectangleHitbox GetHitbox(int index)
        {
            int targetIndex = 0;
            foreach (ANPCHitbox subHitbox in hitboxes)
            {
                if (index - targetIndex < subHitbox.HitboxCount)
                {
                    return subHitbox.GetHitbox(index - targetIndex);
                }
                targetIndex += subHitbox.HitboxCount;
            }
            throw new IndexOutOfRangeException();
        }

        public override void WriteTo(ModPacket packet)
        {
            packet.Write(hitboxes.Count);
            foreach(ANPCHitbox hitbox in hitboxes)
            {
                hitbox.Write(packet);
            }
        }

        public override ANPCHitbox ReadFrom(BinaryReader reader)
        {
            List<ANPCHitbox> hitboxes = new List<ANPCHitbox>();

            int length = reader.ReadInt32();
            for (int i = 0; i < length; i++)
            {
                hitboxes.Add(Read(reader));
            }

            return new MultiHitbox(hitboxes);
        }
    }
}

