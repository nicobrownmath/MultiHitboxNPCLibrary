using System;
using System.Collections;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.ModLoader;

namespace MultiHitboxNPCLibrary
{
	public abstract class ANPCHitbox
    {
        //TODO: Update this with a way to get segment data
        public abstract bool Colliding(NPC npc, Func<Rectangle, bool> collisionCheck);

		public abstract Rectangle BoundingBox();

        public Point InflationFrom(Vector2 center)
        {
            Rectangle boundingBox = BoundingBox();
            return new Point((int)Math.Max(center.X - boundingBox.Left, boundingBox.Right - center.X), (int)Math.Max(center.Y - boundingBox.Top, boundingBox.Bottom - center.Y));
        }

        public abstract ICollection<RectangleHitbox> AllHitboxes(); //returns a collection of all low-level hitboxes for the purposes of random segment drops

        //draw method is meant for debugging
        public virtual void Draw(SpriteBatch spriteBatch, Vector2 screenPos, Color drawColor)
        {
            Rectangle hitbox = BoundingBox();
            spriteBatch.Draw(TextureAssets.MagicPixel.Value, new Rectangle(hitbox.X - (int)screenPos.X, hitbox.Y - (int)screenPos.Y, hitbox.Width, hitbox.Height), drawColor);
        }
    }

    //a basic rectangle hitbox
    public class RectangleHitbox : ANPCHitbox
    {
        public Rectangle hitbox; //the hitbox rectangle
        public int index; //our index in the overall multiHitbox

        public Vector2 velocity; //velocity for the purposes of knockback TODO: player and enemy knockback, actually calculate and store this

        public RectangleHitbox(Rectangle hitbox, int index)
        {
            this.hitbox = hitbox;
            this.index = index;
        }

        public override Rectangle BoundingBox()
        {
            return hitbox;
        }

        public override bool Colliding(NPC npc, Func<Rectangle, bool> collisionCheck)
        {
            if (collisionCheck.Invoke(hitbox))
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
    }

    public interface IMultiHitboxSegmentUpdate
    {
        public void MultiHitboxSegmentUpdate(NPC npc, RectangleHitbox mostRecentHitbox);
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

        public MultiHitbox()
        {
            hitboxes = new List<ANPCHitbox>();
        }

        public static ANPCHitbox AutoAssignFrom(List<Rectangle> rectangles, MultiHitboxAssignmentMode assignmentMode = MultiHitboxAssignmentMode.Nested)
        {
            switch (assignmentMode)
            {
                case MultiHitboxAssignmentMode.Basic:
                    {
                        List<ANPCHitbox> hitboxes = new List<ANPCHitbox>();
                        int index = 0;
                        foreach (Rectangle rectangle in rectangles)
                        {
                            hitboxes.Add(new RectangleHitbox(rectangle, index));
                            index++;
                        }
                        return new MultiHitbox(hitboxes);
                    }
                case MultiHitboxAssignmentMode.Nested:
                    {
                        int index = 0;
                        return NestedHitboxFrom(rectangles, rectangles.Count, ref index);
                    }
            }
            return null; //how did we get here
        }

        static ANPCHitbox NestedHitboxFrom(List<Rectangle> rectangles, int maxDepthToReach, ref int index)
        {
            if (maxDepthToReach == 0)
            {
                throw new ArgumentException("MultiHitbox: hitboxes cannot be empty");
            }

            if (maxDepthToReach == 1)
            {
                RectangleHitbox output = new RectangleHitbox(rectangles[index], index);
                index++;
                return output;
            }

            List<ANPCHitbox> hitboxes = new List<ANPCHitbox>();
            for (int i = 0; i < 2; i++)
            {
                if (index < rectangles.Count)
                    hitboxes.Add(NestedHitboxFrom(rectangles, (maxDepthToReach + 1) / 2, ref index));
            }
            return new MultiHitbox(hitboxes);
        }

        public MultiHitbox(List<ANPCHitbox> hitboxes)
        {
            this.hitboxes = hitboxes;
        }

        public override Rectangle BoundingBox()
        {
            float left = float.MaxValue;
            float right = float.MinValue;
            float top = float.MaxValue;
            float bottom = float.MinValue;
            foreach (ANPCHitbox subHitbox in hitboxes)
            {
                Rectangle subBoundingBox = subHitbox.BoundingBox();
                if (subBoundingBox.Left < left) left = subBoundingBox.Left;
                if (subBoundingBox.Right > right) right = subBoundingBox.Right;
                if (subBoundingBox.Top < top) top = subBoundingBox.Top;
                if (subBoundingBox.Bottom > bottom) bottom = subBoundingBox.Bottom;
            }

            return new Rectangle((int)Math.Floor(left), (int)Math.Floor(top), (int)Math.Ceiling(right) - (int)Math.Floor(left), (int)Math.Ceiling(bottom) - (int)Math.Floor(top));
        }

        public override bool Colliding(NPC npc, Func<Rectangle, bool> collisionCheck)
        {
            if (!collisionCheck.Invoke(BoundingBox())) return false;
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
    }
}

