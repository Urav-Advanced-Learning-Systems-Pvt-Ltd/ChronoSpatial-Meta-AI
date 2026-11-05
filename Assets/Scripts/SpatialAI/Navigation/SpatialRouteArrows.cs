using System.Collections.Generic;
using UnityEngine;

namespace SpatialAI
{
    public sealed class SpatialRouteArrows : MonoBehaviour
    {
        readonly List<LineRenderer> arrows=new List<LineRenderer>();
        Material material;
        void Awake()
        {
            material=new Material(Shader.Find("Sprites/Default")){color=new Color(.1f,1f,.85f,1)};
            for(int i=0;i<6;i++)
            {
                var obj=new GameObject("Route arrow "+i);obj.transform.SetParent(transform,false);
                var line=obj.AddComponent<LineRenderer>();line.sharedMaterial=material;line.useWorldSpace=true;
                line.positionCount=3;line.widthMultiplier=.085f;line.numCapVertices=4;line.numCornerVertices=4;line.enabled=false;
                arrows.Add(line);
            }
        }
        public void Hide() { foreach(var arrow in arrows) arrow.enabled=false; }
        public void Render(SpatialRouteFollower follower,SpatialSDKSession session)
        {
            Hide();
            if(follower==null || !follower.OnRoute || follower.Arrived || !session.TryGetMapPose(out _)) return;
            for(int i=0;i<arrows.Count;i++)
            {
                float at=follower.Progress+.7f+i*.9f;if(at>follower.Length) break;
                var centre=follower.PointAt(at)+Vector3.up*.05f;
                var direction=(follower.PointAt(Mathf.Min(at+.3f,follower.Length))-follower.PointAt(Mathf.Max(0,at-.3f))).normalized;
                var right=Vector3.Cross(Vector3.up,direction);
                var points=new[]{centre-direction*.23f-right*.22f,centre+direction*.15f,centre-direction*.23f+right*.22f};
                for(int p=0;p<points.Length;p++) if(!session.TryMapToWorld(points[p],out points[p])) return;
                arrows[i].SetPositions(points);arrows[i].enabled=true;
            }
        }
        void OnDisable() { Hide(); }
        void OnDestroy() { if(material!=null)Destroy(material); }
    }
}
