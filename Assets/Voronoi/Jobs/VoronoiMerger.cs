﻿// ReSharper disable CheckNamespace
using System;
using Unity.Burst;
using Voronoi.Structures;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Voronoi
{
    [BurstCompile(CompileSynchronously = true)]
    internal struct VoronoiMerger : IJob
    {
        #region Left voronoi data

        /// <summary>
        /// Left voronoi sites collection
        /// </summary>
        public NativeArray<VSite> LeftSites;
        
        /// <summary>
        /// Left voronoi edges collection
        /// </summary>
        public NativeList<VEdge> LeftEdges;
        
        /// <summary>
        /// Left diagram convex hull collection where elements is site indexes
        /// </summary>
        public NativeList<VSite> LeftConvexHull;
        
        /// <summary>
        /// Dictionary of left diagram where key is site id and value is site array index 
        /// </summary>
        public NativeHashMap<int, int> LeftSiteIdIndexes;
        
        /// <summary>
        /// Associative array where key is site id and value is edge indexes of left diagram
        /// </summary>
        public NativeMultiHashMap<int, int> LeftRegions;
        
        #endregion

        #region Right voronoi data

        /// <summary>
        /// Right voronoi sites collection
        /// </summary>
        public NativeArray<VSite> RightSites;
        
        /// <summary>
        /// Right voronoi edges collection
        /// </summary>
        public NativeList<VEdge> RightEdges;
        
        /// <summary>
        /// Right diagram convex hull collection where elements is site indexes  
        /// </summary>
        public NativeList<VSite> RightConvexHull;
        
        /// <summary>
        /// Dictionary of right diagram where key is site id and value is site array index  
        /// </summary>
        public NativeHashMap<int, int> RightSiteIdIndexes;
        
        /// <summary>
        /// Associative array where key is site id and value is edge indexes of right diagram
        /// </summary>
        public NativeMultiHashMap<int, int> RightRegions;

        #endregion

        #region Output data

        public NativeArray<VSite> Sites;
        public NativeList<VEdge> Edges;
        public NativeList<VSite> ConvexHull;
        public NativeHashMap<int, int> SiteIdIndexes;
        public NativeMultiHashMap<int, int> Regions;

        #endregion


        private NativeHashMap<int, float2> regionEnterPoints;
        private NativeHashMap<int, VEdge> regionEnterEdges;
        private NativeHashMap<int, int> regionEnterEdgesIndexes;

        private NativeHashMap<int, byte> leftEdgeIndexesToRemove;
        private NativeHashMap<int, byte> rightEdgeIndexesToRemove;

        private double2 currentPoint;
        private VEdge currentEdge;
        private int currentEdgeIndex;

        private VSite left;
        private VSite right;

        private NativeList<VEdge> newEdges;

        public void Execute()
        {
            const int capacity = 1024;
            newEdges = new NativeList<VEdge>(capacity, Allocator.Temp);

            regionEnterPoints = new NativeHashMap<int, float2>(capacity, Allocator.Temp);
            regionEnterEdges = new NativeHashMap<int, VEdge>(capacity, Allocator.Temp);
            regionEnterEdgesIndexes = new NativeHashMap<int, int>(capacity, Allocator.Temp);;
            
            var temp = new NativeList<double2>(4, Allocator.Temp);

            leftEdgeIndexesToRemove = new NativeHashMap<int, byte>(capacity, Allocator.Temp);
            rightEdgeIndexesToRemove = new NativeHashMap<int, byte>(capacity, Allocator.Temp);

            // merge sites
            NativeArray<VSite>.Copy(LeftSites, 0, Sites, 0, LeftSites.Length);
            NativeArray<VSite>.Copy(RightSites, 0, Sites, LeftSites.Length, RightSites.Length);
            for (var i = 0; i < LeftSites.Length; i++) SiteIdIndexes[LeftSites[i].Id] = i;
            for (var i = 0; i < RightSites.Length; i++) SiteIdIndexes[RightSites[i].Id] = i + LeftSites.Length;

            // merge convex hulls and find upper and lower tangents
            ConvexHull.AddRange(Voronoi.ConvexHull.Merge(LeftConvexHull, RightConvexHull, 
                out var lLeft, out var lRight,
                out var qLeft, out var qRight));

            left = lLeft;
            right = lRight;

            // incoming ray
            var middle = (left.Point + right.Point) * 0.5f;
            var rayDir = VGeometry.Perpendicular(right.Point - left.Point);
            var leftDistance = RayRegionCrossing(middle, rayDir, left, ref LeftEdges, ref LeftRegions,
                out var leftPoint, out var leftEdgeIndex, out var leftEdge);
            var rightDistance = RayRegionCrossing(middle, rayDir, right, ref RightEdges, ref RightRegions,
                out var rightPoint, out var rightEdgeIndex, out var rightEdge);
            VEdge startEdge;
            if (leftDistance < rightDistance)
            {
                currentPoint = leftPoint;
                currentEdge = leftEdge;
                currentEdgeIndex = leftEdgeIndex;
                var end = VGeometry.BuildRayEnd(currentPoint, right.Point, left.Point, temp);
                startEdge = new VEdge(currentPoint, end, left.Id, right.Id);
                currentEdge = CutLeftEdge(end, VEdge.Null, currentPoint, currentEdge);
                LeftEdges[currentEdgeIndex] = currentEdge;
                var enumerator = LeftRegions.GetValuesForKey(left.Id);
                while (enumerator.MoveNext())
                {
                    var edgeIndex = enumerator.Current;
                    if (edgeIndex == currentEdgeIndex) continue;
                    var dir = GetEdgeSideLeft(end, currentPoint, edgeIndex);
                    if (dir > 0) leftEdgeIndexesToRemove.TryAdd(edgeIndex, 0);
                }
                enumerator.Dispose();
                left = LeftSites[LeftSiteIdIndexes[leftEdge.Left == left.Id ? leftEdge.Right : leftEdge.Left]];
                regionEnterPoints[left.Id] = (float2) currentPoint;
                regionEnterEdges[left.Id] = currentEdge;
                regionEnterEdgesIndexes[left.Id] = currentEdgeIndex;
            }
            else
            {
                currentPoint = rightPoint;
                currentEdge = rightEdge;
                currentEdgeIndex = rightEdgeIndex;
                var end = VGeometry.BuildRayEnd(currentPoint, right.Point, left.Point, temp);
                startEdge = new VEdge(currentPoint, end, left.Id, right.Id);
                currentEdge = CutRightEdge(end, VEdge.Null, currentPoint, currentEdge);
                RightEdges[currentEdgeIndex] = currentEdge;
                var enumerator = RightRegions.GetValuesForKey(right.Id);
                while (enumerator.MoveNext())
                {
                    var edgeIndex = enumerator.Current;
                    if (edgeIndex == currentEdgeIndex) continue;
                    var dir = GetEdgeSideRight(end, currentPoint, edgeIndex);
                    if (dir < 1) rightEdgeIndexesToRemove.TryAdd(edgeIndex, 0);
                }
                enumerator.Dispose();
                right = RightSites[RightSiteIdIndexes[rightEdge.Left == right.Id ? rightEdge.Right : rightEdge.Left]];
                regionEnterPoints[right.Id] = (float2) currentPoint;
                regionEnterEdges[right.Id] = currentEdge;
                regionEnterEdgesIndexes[right.Id] = currentEdgeIndex;
            }
            newEdges.Add(startEdge);
            
            regionEnterPoints[startEdge.Left] = startEdge.End;
            regionEnterPoints[startEdge.Right] = startEdge.End;
            regionEnterEdges[startEdge.Left] = VEdge.Null;
            regionEnterEdges[startEdge.Right] = VEdge.Null;
            regionEnterEdgesIndexes[startEdge.Left] = -1;
            regionEnterEdgesIndexes[startEdge.Right] = -1;
            

            // edges
            while (!(left == qLeft && right == qRight))
            {
                var perp = VGeometry.Perpendicular(right.Point - left.Point);
                var lCrossed = RegionCrossing(currentPoint, perp, left,
                    ref LeftEdges, ref LeftRegions, ref currentEdge,
                    out var lDistance, out var lVertex, out leftEdgeIndex, out var lEdge);
                var rCrossed = RegionCrossing(currentPoint, perp, right, 
                    ref RightEdges, ref RightRegions, ref currentEdge,
                    out var rDistance, out var rVertex, out rightEdgeIndex, out var rEdge);

                if (!lCrossed && !rCrossed)
                    throw new Exception("Voronoi merge error: no crossing");

                var leftId = left.Id;
                var rightId = right.Id;

                if (lCrossed && rCrossed && VGeometry.Float2Equals(lVertex, rVertex))
                {
                    var newEdge = new VEdge(currentPoint, lVertex, leftId, rightId);
                    newEdges.Add(newEdge);
                    HandleLeftEdge(leftId, newEdge.End, newEdge.End, lEdge, leftEdgeIndex);
                    HandleRightEdge(rightId, newEdge.End, newEdge.End, rEdge, rightEdgeIndex);
                    continue;
                }

                if (lDistance < rDistance)
                {
                    var newEdge = new VEdge(currentPoint, lVertex, leftId, rightId);
                    newEdges.Add(newEdge);
                    HandleLeftEdge(leftId, newEdge.End, lVertex, lEdge, leftEdgeIndex);
                }
                else
                {
                    var newEdge = new VEdge(currentPoint, rVertex, leftId, rightId);
                    newEdges.Add(newEdge);
                    HandleRightEdge(rightId, newEdge.End, rVertex, rEdge, rightEdgeIndex);
                }
            }

            
            // outgoing ray
            var endPoint = VGeometry.BuildRayEnd((left.Point + right.Point) * 0.5f, 
                left.Point, right.Point, temp);
            newEdges.Add(new VEdge(currentPoint, endPoint, left.Id, right.Id));


            #region Merge data

            // remove old edges
            var leftEdgeIndexes = leftEdgeIndexesToRemove.GetKeyArray(Allocator.Temp);
            leftEdgeIndexes.Sort();
            for (var i = leftEdgeIndexes.Length - 1; i >= 0; i--) LeftEdges.RemoveAtSwapBack(leftEdgeIndexes[i]);
            var rightEdgeIndexes = rightEdgeIndexesToRemove.GetKeyArray(Allocator.Temp);
            rightEdgeIndexes.Sort();
            for (var i = rightEdgeIndexes.Length - 1; i >= 0; i--) RightEdges.RemoveAtSwapBack(rightEdgeIndexes[i]);

            // merge edges and regions
            for (var i = 0; i < LeftEdges.Length; i++)
            {  
                Regions.Add(LeftEdges[i].Left, i);
                Regions.Add(LeftEdges[i].Right, i);
            }
            Edges.AddRange(LeftEdges);

            for (var i = 0; i < newEdges.Length; i++)
            {
                var index = Edges.Length + i;
                Regions.Add(newEdges[i].Left, index);
                Regions.Add(newEdges[i].Right, index);
            }
            Edges.AddRange(newEdges);

            for (var i = 0; i < RightEdges.Length; i++)
            {
                var index = Edges.Length + i;
                Regions.Add(RightEdges[i].Left, index);
                Regions.Add(RightEdges[i].Right, index);
            }
            Edges.AddRange(RightEdges);
            
            #endregion

            // Если точки имеют одинаковую координату X, то стоит их сортировать по координате Y,
            // таким образом, чтобы равномерно и последовательно их разделить.
        }


        private void HandleLeftEdge(int siteId, float2 exitPoint, double2 targetVertex, VEdge targetEdge,
            int targetEdgeIndex)
        {
            left = LeftSites[LeftSiteIdIndexes[targetEdge.Left == siteId ? targetEdge.Right : targetEdge.Left]];
            currentPoint = targetVertex;
            currentEdge = targetEdge;
            currentEdgeIndex = targetEdgeIndex;
                    
            // region exit
            var enterPoint = regionEnterPoints[siteId];
            var enterEdge = regionEnterEdges[siteId];
            var enterEdgeIndex = regionEnterEdgesIndexes[siteId];
        
            LeftEdges[currentEdgeIndex] = CutLeftEdge(enterPoint, enterEdge, exitPoint, currentEdge);
        
            var enumerator = LeftRegions.GetValuesForKey(siteId);
            while (enumerator.MoveNext())
            {
                var edgeIndex = enumerator.Current;
                if (edgeIndex == currentEdgeIndex) continue;
                if (edgeIndex == enterEdgeIndex) continue;
                var dir = GetEdgeSideLeft(enterPoint, exitPoint, edgeIndex);
                if (dir > 0) leftEdgeIndexesToRemove.TryAdd(edgeIndex, 0);
            }
            enumerator.Dispose();
                    
            // region enter
            regionEnterPoints[left.Id] = (float2) currentPoint;
            regionEnterEdges[left.Id] = currentEdge;
            regionEnterEdgesIndexes[left.Id] = currentEdgeIndex;
        }

        private void HandleRightEdge(int siteId, float2 exitPoint, double2 targetVertex, VEdge targetEdge,
            int targetEdgeIndex)
        {
            right = RightSites[RightSiteIdIndexes[targetEdge.Left == siteId ? targetEdge.Right : targetEdge.Left]];
            currentPoint = targetVertex;
            currentEdge = targetEdge;
            currentEdgeIndex = targetEdgeIndex;
                    
            // region exit
            var enterPoint = regionEnterPoints[siteId];
            var enterEdge = regionEnterEdges[siteId];
            var enterEdgeIndex = regionEnterEdgesIndexes[siteId];

            RightEdges[currentEdgeIndex] = CutRightEdge(enterPoint, enterEdge, exitPoint, currentEdge);

            var enumerator = RightRegions.GetValuesForKey(siteId);
            while (enumerator.MoveNext())
            {
                var edgeIndex = enumerator.Current;
                if (edgeIndex == currentEdgeIndex) continue;
                if (edgeIndex == enterEdgeIndex) continue;
                var dir = GetEdgeSideRight(enterPoint, exitPoint, edgeIndex);
                if (dir < 0) rightEdgeIndexesToRemove.TryAdd(edgeIndex, 0);
            }
            enumerator.Dispose();
                    
            // region enter
            regionEnterPoints[right.Id] = (float2) currentPoint;
            regionEnterEdges[right.Id] = currentEdge;
            regionEnterEdgesIndexes[right.Id] = currentEdgeIndex;
        }
        

        private static double RayRegionCrossing(
            float2 middle, double2 normal, VSite site,
            ref NativeList<VEdge> edges,
            ref NativeMultiHashMap<int, int> regions,
            out double2 crossingVertex, out int crossedEdgeIndex, out VEdge crossedEdge)
        {
            var distance = double.MaxValue;
            var minPoint = double2.zero;
            var minEdge = new VEdge();
            var minEdgeIndex = -1;
            var atan = math.atan2(normal.x, normal.y);
            var cos = math.cos(atan);
            var sin = math.sin(atan);
            var a = middle;
            var b = middle + normal;

            var region = regions.GetValuesForKey(site.Id);
            while (region.MoveNext())
            {
                var edgeIndex = region.Current;
                var edge = edges[edgeIndex];
                var c = edge.Start;
                var d = edge.End;
                if (!VGeometry.Intersection(a, b, c, d, out var point)) continue;
                var offset = point - middle;
                var aligned = new double2( offset.x * cos - offset.y * sin,  offset.x * sin + offset.y * cos);
                if (aligned.y > distance) continue;
                if (!VGeometry.PointOnLineSegment(c, d, point)) continue;
                distance = aligned.y;
                minPoint = point;
                minEdge = edge;
                minEdgeIndex = edgeIndex;
            }
            region.Dispose();

            crossingVertex = minPoint;
            crossedEdge = minEdge;
            crossedEdgeIndex = minEdgeIndex;
            return distance;
        }

        private bool RegionCrossing(
            double2 start,
            double2 dir,
            VSite site,
            ref NativeList<VEdge> edges,
            ref NativeMultiHashMap<int, int> regions,
            ref VEdge currentEdge,
            out double approach,
            out double2 crossingVertex,
            out int crossedEdgeIndex,
            out VEdge crossedEdge)
        {
            var minApproach = double.MaxValue;
            var minPoint = double2.zero;
            var minEdge = new VEdge();
            var minEdgeIndex = -1;
            var a = start;
            var b = start + dir;
            var crossed = false;
            
            var region = regions.GetValuesForKey(site.Id);
            while (region.MoveNext())
            {
                var edgeIndex = region.Current;
                var edge = edges[edgeIndex];
                if (edge.Equals(currentEdge)) continue;
                var c = edge.Start;
                var d = edge.End;
                if (!VGeometry.Intersection(a, b, c, d, out var point)) continue;
                var delta = point - start;
                var apr = math.dot(delta, delta);
                if (minApproach < apr || math.dot(dir, delta) <= 0) continue;
                if (!VGeometry.PointOnLineSegment(c, d, point)) continue;
                minApproach = apr;
                minPoint = point;
                minEdge = edge;    
                minEdgeIndex = edgeIndex;
                crossed = true;
            }
            region.Dispose();

            crossingVertex = minPoint;
            crossedEdge = minEdge;
            approach = minApproach;
            crossedEdgeIndex = minEdgeIndex;
            return crossed;
        }

        private int GetEdgeSideLeft(double2 enterPoint, double2 exitPoint, int edgeIndex)
        {
            var edge = LeftEdges[edgeIndex];
            return math.max(
                VGeometry.RaySide(enterPoint, exitPoint, edge.End),
                VGeometry.RaySide(enterPoint, exitPoint, edge.Start));
        }
        
        private int GetEdgeSideRight(double2 enterPoint, double2 exitPoint, int edgeIndex)
        {
            var edge = RightEdges[edgeIndex];
            return math.min(
                VGeometry.RaySide(enterPoint, exitPoint, edge.End),
                VGeometry.RaySide(enterPoint, exitPoint, edge.Start));
        }

        private static VEdge CutLeftEdge(double2 enterPoint, VEdge enterEdge, double2 exitPoint, VEdge exitEdge)
        {
            if (enterEdge.Equals(exitEdge))
                return new VEdge(enterPoint, exitPoint, exitEdge.Left, exitEdge.Right);
            
            return VGeometry.RaySide(enterPoint, exitPoint, exitEdge.Start) <
                   VGeometry.RaySide(enterPoint, exitPoint, exitEdge.End) 
                ? new VEdge(exitEdge.Start, exitPoint, exitEdge.Left, exitEdge.Right)
                : new VEdge(exitEdge.End, exitPoint, exitEdge.Left, exitEdge.Right);
        }
        
        private static VEdge CutRightEdge(double2 enterPoint, VEdge enterEdge, double2 exitPoint, VEdge exitEdge)
        {
            if (enterEdge.Equals(exitEdge))
                return new VEdge(enterPoint, exitPoint, exitEdge.Left, exitEdge.Right);

            return VGeometry.RaySide(enterPoint, exitPoint, exitEdge.Start) >
                   VGeometry.RaySide(enterPoint, exitPoint, exitEdge.End)
                ? new VEdge(exitEdge.Start, exitPoint, exitEdge.Left, exitEdge.Right)
                : new VEdge(exitEdge.End, exitPoint, exitEdge.Left, exitEdge.Right);
        }

        public void Dispose()
        {
            Sites.Dispose();
            Edges.Dispose();
            Regions.Dispose();
            SiteIdIndexes.Dispose();
            ConvexHull.Dispose();
        }

        public static VoronoiMerger CreateJob(FortunesWithConvexHull left, FortunesWithConvexHull right)
        {
            var sites = new NativeArray<VSite>(left.Sites.Length + right.Sites.Length, Allocator.Persistent);
            var edges = new NativeList<VEdge>(left.Edges.Capacity + right.Edges.Capacity, Allocator.Persistent);
            var regions = new NativeMultiHashMap<int, int>(left.Regions.Capacity, Allocator.Persistent);
            var siteIdIndexes = new NativeHashMap<int, int>(left.SiteIdIndexes.Capacity, Allocator.Persistent);
            var convexHull = new NativeList<VSite>(left.ConvexHull.Capacity + right.ConvexHull.Capacity, Allocator.Persistent);

            return new VoronoiMerger
            {
                LeftSites = left.Sites,
                LeftEdges = left.Edges,
                LeftRegions = left.Regions,
                LeftSiteIdIndexes = left.SiteIdIndexes,
                LeftConvexHull = left.ConvexHull,

                RightSites = right.Sites,
                RightEdges = right.Edges,
                RightRegions = right.Regions,
                RightSiteIdIndexes = right.SiteIdIndexes,
                RightConvexHull = right.ConvexHull,

                Sites = sites,
                Edges = edges,
                Regions = regions,
                SiteIdIndexes = siteIdIndexes,
                ConvexHull = convexHull
            };
        }

        public static VoronoiMerger CreateJob(VoronoiMerger left, VoronoiMerger right)
        {
            var sites = new NativeArray<VSite>(left.Sites.Length + right.Sites.Length, Allocator.Persistent);
            var edges = new NativeList<VEdge>(left.Edges.Capacity + right.Edges.Capacity, Allocator.Persistent);
            var regions =
                new NativeMultiHashMap<int, int>(left.Regions.Capacity + right.Regions.Capacity, Allocator.Persistent);
            var siteIdIndexes = new NativeHashMap<int, int>(left.SiteIdIndexes.Capacity + right.SiteIdIndexes.Capacity,
                Allocator.Persistent);
            var convexHull = new NativeList<VSite>(left.ConvexHull.Capacity + right.ConvexHull.Capacity,
                Allocator.Persistent);

            return new VoronoiMerger
            {
                LeftSites = left.Sites,
                LeftEdges = left.Edges,
                LeftRegions = left.Regions,
                LeftSiteIdIndexes = left.SiteIdIndexes,
                LeftConvexHull = left.ConvexHull,

                RightSites = right.Sites,
                RightEdges = right.Edges,
                RightRegions = right.Regions,
                RightSiteIdIndexes = right.SiteIdIndexes,
                RightConvexHull = right.ConvexHull,

                Sites = sites,
                Edges = edges,
                Regions = regions,
                SiteIdIndexes = siteIdIndexes,
                ConvexHull = convexHull
            };
        }
    }
}