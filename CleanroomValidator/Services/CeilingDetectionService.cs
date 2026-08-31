using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CleanroomValidator.Services
{
    /// <summary>
    /// Service to detect ceilings and calculate effective room height
    /// </summary>
    public class CeilingDetectionService
    {
        private readonly Document _doc;
        private readonly List<Document> _linkedDocs;

        public CeilingDetectionService(Document doc, List<Document> linkedDocs = null)
        {
            _doc = doc;
            _linkedDocs = linkedDocs ?? new List<Document>();
        }

        /// <summary>
        /// Result of ceiling detection for a room
        /// </summary>
        public class CeilingResult
        {
            public bool HasCeiling { get; set; }
            public double AverageHeight { get; set; } // feet from floor
            public double MinHeight { get; set; }
            public double MaxHeight { get; set; }
            public double CoveragePercentage { get; set; } // 0-100
            public bool IsSloped { get; set; }
            public bool IsPartialCoverage { get; set; }
            public bool IsFromLinkedFile { get; set; }
            public string Notes { get; set; }
        }

        /// <summary>
        /// Detect ceiling for a room and calculate average height
        /// </summary>
        public CeilingResult DetectCeiling(Room room)
        {
            var result = new CeilingResult
            {
                HasCeiling = false,
                AverageHeight = 0,
                MinHeight = double.MaxValue,
                MaxHeight = 0,
                CoveragePercentage = 0,
                Notes = ""
            };

            // Get room boundary
            var roomBoundary = GetRoomBoundary(room);
            if (roomBoundary == null || !roomBoundary.Any())
            {
                result.Notes = "Could not determine room boundary";
                return result;
            }

            var roomLevel = room.Level;
            if (roomLevel == null)
            {
                result.Notes = "Room has no level";
                return result;
            }

            double floorElevation = roomLevel.Elevation;
            double roomArea = room.Area; // sq ft

            // Find ceilings in main document
            var ceilings = FindCeilingsAboveRoom(room, _doc, floorElevation);

            // Check linked documents for ceilings
            foreach (var linkedDoc in _linkedDocs)
            {
                var linkedCeilings = FindCeilingsAboveRoom(room, linkedDoc, floorElevation);
                if (linkedCeilings.Any())
                {
                    ceilings.AddRange(linkedCeilings);
                    result.IsFromLinkedFile = true;
                }
            }

            if (!ceilings.Any())
            {
                result.Notes = "No ceiling found - using full room height";
                return result;
            }

            // Calculate ceiling heights and coverage
            var ceilingHeights = new List<double>();
            double totalCeilingArea = 0;

            foreach (var ceiling in ceilings)
            {
                var ceilingInfo = GetCeilingHeightInfo(ceiling, room, floorElevation);
                if (ceilingInfo.area > 0)
                {
                    ceilingHeights.Add(ceilingInfo.avgHeight);
                    totalCeilingArea += ceilingInfo.area;

                    if (ceilingInfo.minHeight < result.MinHeight)
                        result.MinHeight = ceilingInfo.minHeight;
                    if (ceilingInfo.maxHeight > result.MaxHeight)
                        result.MaxHeight = ceilingInfo.maxHeight;
                }
            }

            if (ceilingHeights.Any())
            {
                result.HasCeiling = true;
                
                // Calculate weighted average height based on area coverage
                result.AverageHeight = ceilingHeights.Average();
                result.CoveragePercentage = Math.Min(100, (totalCeilingArea / roomArea) * 100);
                
                // Check if sloped (significant difference between min and max)
                double heightDifference = result.MaxHeight - result.MinHeight;
                result.IsSloped = heightDifference > 0.5; // More than 6 inches difference
                
                // Check partial coverage
                result.IsPartialCoverage = result.CoveragePercentage < 90;

                if (result.IsSloped)
                    result.Notes = "Sloped ceiling detected - using average height";
                else if (result.IsPartialCoverage)
                    result.Notes = $"Partial ceiling coverage ({result.CoveragePercentage:F0}%)";
                else
                    result.Notes = "Standard ceiling";
            }

            // If min/max weren't set, use average
            if (result.MinHeight == double.MaxValue)
                result.MinHeight = result.AverageHeight;
            if (result.MaxHeight == 0)
                result.MaxHeight = result.AverageHeight;

            return result;
        }

        /// <summary>
        /// Calculate effective volume considering ceiling
        /// </summary>
        public double CalculateEffectiveVolume(Room room, bool isClassified)
        {
            double roomArea = room.Area; // sq ft
            
            if (!isClassified)
            {
                // Unclassified rooms use full volume
                var volumeParam = room.get_Parameter(BuiltInParameter.ROOM_VOLUME);
                return volumeParam?.AsDouble() ?? 0;
            }

            // For classified rooms, consider ceiling height
            var ceilingResult = DetectCeiling(room);
            
            if (!ceilingResult.HasCeiling)
            {
                // No ceiling - use full room volume
                var volumeParam = room.get_Parameter(BuiltInParameter.ROOM_VOLUME);
                return volumeParam?.AsDouble() ?? 0;
            }

            // Calculate volume based on ceiling height
            // Volume = Area × (Ceiling Height)
            return roomArea * ceilingResult.AverageHeight;
        }

        private List<CurveLoop> GetRoomBoundary(Room room)
        {
            try
            {
                var options = new SpatialElementBoundaryOptions();
                var boundaries = room.GetBoundarySegments(options);
                
                if (boundaries == null || !boundaries.Any())
                    return null;

                var loops = new List<CurveLoop>();
                foreach (var boundaryList in boundaries)
                {
                    var curves = new List<Curve>();
                    foreach (var segment in boundaryList)
                    {
                        curves.Add(segment.GetCurve());
                    }
                    if (curves.Any())
                    {
                        loops.Add(CurveLoop.Create(curves));
                    }
                }
                return loops;
            }
            catch
            {
                return null;
            }
        }

        private List<Ceiling> FindCeilingsAboveRoom(Room room, Document doc, double floorElevation)
        {
            var result = new List<Ceiling>();
            
            try
            {
                var roomLocation = room.Location as LocationPoint;
                if (roomLocation == null) return result;

                var roomPoint = roomLocation.Point;
                double roomUpperLimit = floorElevation + 20; // Search up to 20 feet above floor

                var collector = new FilteredElementCollector(doc)
                    .OfClass(typeof(Ceiling))
                    .Cast<Ceiling>();

                foreach (var ceiling in collector)
                {
                    try
                    {
                        var ceilingBBox = ceiling.get_BoundingBox(null);
                        if (ceilingBBox == null) continue;

                        // Check if ceiling is above floor and within reasonable height
                        if (ceilingBBox.Min.Z < floorElevation || ceilingBBox.Min.Z > roomUpperLimit)
                            continue;

                        // Check if ceiling overlaps room horizontally
                        if (IsCeilingOverRoom(ceiling, room))
                        {
                            result.Add(ceiling);
                        }
                    }
                    catch
                    {
                        // Skip problematic ceilings
                    }
                }
            }
            catch
            {
                // Return empty list on error
            }

            return result;
        }

        private bool IsCeilingOverRoom(Ceiling ceiling, Room room)
        {
            try
            {
                var roomLocation = room.Location as LocationPoint;
                if (roomLocation == null) return false;

                var roomPoint = roomLocation.Point;
                var ceilingBBox = ceiling.get_BoundingBox(null);
                
                if (ceilingBBox == null) return false;

                // Simple bounding box check
                return roomPoint.X >= ceilingBBox.Min.X - 1 &&
                       roomPoint.X <= ceilingBBox.Max.X + 1 &&
                       roomPoint.Y >= ceilingBBox.Min.Y - 1 &&
                       roomPoint.Y <= ceilingBBox.Max.Y + 1;
            }
            catch
            {
                return false;
            }
        }

        private (double avgHeight, double minHeight, double maxHeight, double area) GetCeilingHeightInfo(
            Ceiling ceiling, Room room, double floorElevation)
        {
            try
            {
                var ceilingBBox = ceiling.get_BoundingBox(null);
                if (ceilingBBox == null)
                    return (0, 0, 0, 0);

                // Get ceiling bottom elevation relative to floor
                double ceilingZ = ceilingBBox.Min.Z;
                double height = ceilingZ - floorElevation;

                // Estimate ceiling area from bounding box
                double width = ceilingBBox.Max.X - ceilingBBox.Min.X;
                double length = ceilingBBox.Max.Y - ceilingBBox.Min.Y;
                double area = width * length;

                // For simple ceilings, min = max = avg
                // For more complex analysis, we'd sample multiple points
                return (height, height, height, area);
            }
            catch
            {
                return (0, 0, 0, 0);
            }
        }
    }
}
