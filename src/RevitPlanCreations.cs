using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;

namespace RevitCommandBridge
{
    /// <summary>
    /// 所有 Revit 元素创建操作的实现。
    /// Implementation of all Revit element creation operations.
    /// </summary>
    internal static class RevitPlanCreations
    {
        /// <summary>
        /// 创建标高。校验名称唯一性并支持预览模式。
        /// Create a level. Validates name uniqueness and supports preview mode.
        /// </summary>
        public static Dictionary<string, object> CreateLevel(PlanStep step, PlanExecutionContext context)
        {
            // 从参数中获取标高值（毫米）
            double elevationMm = PlanValues.RequireMillimeters(step.Arguments, "elevation_mm", "elevation");
            string name = PlanValues.String(step.Arguments, null, "name");
            // 检查名称是否已存在（不区分大小写）
            if (!string.IsNullOrWhiteSpace(name) && new FilteredElementCollector(context.Document)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .Any(existingLevel => string.Equals(existingLevel.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new BridgeCommandException("标高名称已存在：" + name);
            }
            var data = new Dictionary<string, object>
            {
                { "name", name },
                { "elevation_mm", elevationMm }
            };
            // 预览模式：返回参数但不执行创建
            if (context.Preview)
            {
                return data;
            }

            Level level = Level.Create(context.Document, PlanValues.ToFeet(elevationMm));
            if (!string.IsNullOrWhiteSpace(name))
            {
                level.Name = name;
            }
            data["element_id"] = level.Id.Value;
            data["element_ids"] = new[] { level.Id.Value };
            data["name"] = level.Name;
            return data;
        }

        /// <summary>
        /// 创建直线轴网。校验起点/终点不重合且 Z 值相同。
        /// Create a straight grid line. Validates start/end are not coincident and share the same Z value.
        /// </summary>
        public static Dictionary<string, object> CreateGrid(PlanStep step, PlanExecutionContext context)
        {
            XYZ start = PlanValues.Point(step.Arguments, "start");
            XYZ end = PlanValues.Point(step.Arguments, "end");
            string name = PlanValues.String(step.Arguments, null, "name");
            if (start.DistanceTo(end) < 1e-8)
            {
                throw new BridgeCommandException("轴网起点与终点不能重合。");
            }
            if (Math.Abs(start.Z - end.Z) > 1e-8)
            {
                throw new BridgeCommandException("直线轴网的 start.z 与 end.z 必须相同。");
            }
            var data = new Dictionary<string, object>
            {
                { "name", name },
                { "start", PlanValues.PointData(start) },
                { "end", PlanValues.PointData(end) }
            };
            if (context.Preview)
            {
                return data;
            }

            Grid grid = Grid.Create(context.Document, Line.CreateBound(start, end));
            if (!string.IsNullOrWhiteSpace(name))
            {
                grid.Name = name;
            }
            data["element_id"] = grid.Id.Value;
            data["element_ids"] = new[] { grid.Id.Value };
            data["name"] = grid.Name;
            return data;
        }

        /// <summary>
        /// 创建墙体。支持指定厚度自动生成新墙类型。
        /// Create a wall. Supports auto-generating a new wall type for a specified thickness.
        /// </summary>
        public static Dictionary<string, object> CreateWall(PlanStep step, PlanExecutionContext context)
        {
            XYZ start = PlanValues.Point(step.Arguments, "start");
            XYZ end = PlanValues.Point(step.Arguments, "end");
            double heightMm = PlanValues.Millimeters(step.Arguments, 3000.0, "height_mm", "height");
            double baseOffsetMm = PlanValues.Millimeters(step.Arguments, 0.0, "base_offset_mm", "base_offset");
            if (start.DistanceTo(end) < 1e-8)
            {
                throw new BridgeCommandException("墙体起点与终点不能重合。");
            }
            if (heightMm <= 0.0)
            {
                throw new BridgeCommandException("墙体 height_mm 必须大于 0。");
            }

            Level level = RevitLookups.ResolveLevel(context.Document, step.Arguments);
            WallType sourceType = (WallType)RevitLookups.ResolveElementType(
                context.Document, typeof(WallType), step.Arguments, true);
            if (sourceType.Kind != WallKind.Basic)
            {
                throw new BridgeCommandException("create_wall 目前只支持 Basic WallType。");
            }
            double requestedThicknessMm = PlanValues.Millimeters(step.Arguments, -1.0, "thickness_mm", "thickness");
            string requestedNewTypeName = PlanValues.String(step.Arguments, null, "new_type", "new_wall_type");
            // 如果指定了厚度，生成目标类型名称：前缀 RCB_ + 源类型 + 厚度
            string targetTypeName = requestedThicknessMm > 0.0
                ? (string.IsNullOrWhiteSpace(requestedNewTypeName)
                    ? "RCB_" + sourceType.Name + "_" + requestedThicknessMm.ToString("0.###", CultureInfo.InvariantCulture) + "mm"
                    : requestedNewTypeName)
                : sourceType.Name;

            var data = new Dictionary<string, object>
            {
                { "level", level.Name },
                { "type_source", sourceType.Name },
                { "type_target", targetTypeName },
                { "start", PlanValues.PointData(start) },
                { "end", PlanValues.PointData(end) },
                { "height_mm", heightMm },
                { "base_offset_mm", baseOffsetMm }
            };
            if (requestedThicknessMm > 0.0)
            {
                data["thickness_mm"] = requestedThicknessMm;
            }
            if (context.Preview)
            {
                return data;
            }

            // 如果需要特定厚度，创建或复用对应的墙类型
            WallType targetType = requestedThicknessMm > 0.0
                ? ResolveOrCreateWallType(context.Document, sourceType, targetTypeName, requestedThicknessMm)
                : sourceType;
            XYZ wallStart = new XYZ(start.X, start.Y, level.Elevation);
            XYZ wallEnd = new XYZ(end.X, end.Y, level.Elevation);
            Wall wall = Wall.Create(
                context.Document,
                Line.CreateBound(wallStart, wallEnd),
                targetType.Id,
                level.Id,
                PlanValues.ToFeet(heightMm),
                PlanValues.ToFeet(baseOffsetMm),
                false,
                false);
            data["element_id"] = wall.Id.Value;
            data["element_ids"] = new[] { wall.Id.Value };
            data["type_target"] = targetType.Name;
            return data;
        }

        /// <summary>
        /// 创建楼板。通过闭合轮廓生成楼板几何。
        /// Create a floor. Generates floor geometry from a closed boundary profile.
        /// </summary>
        public static Dictionary<string, object> CreateFloor(PlanStep step, PlanExecutionContext context)
        {
            Level level = RevitLookups.ResolveLevel(context.Document, step.Arguments);
            FloorType floorType = ResolveFloorType(context.Document, step.Arguments);
            bool structural = PlanValues.Boolean(step.Arguments, false, "structural", "is_structural");
            double offsetMm = PlanValues.Millimeters(step.Arguments, 0.0, "offset_mm", "offset");
            CurveArray profile = BuildClosedProfile(
                step.Arguments,
                level.Elevation + PlanValues.ToFeet(offsetMm),
                "create_floor");
            var data = new Dictionary<string, object>
            {
                { "level", level.Name },
                { "type", floorType.Name },
                { "type_id", floorType.Id.Value },
                { "structural", structural },
                { "offset_mm", offsetMm },
                { "boundary_segment_count", profile.Size }
            };
            if (context.Preview)
            {
                return data;
            }

            var floorProfile = new CurveLoop();
            foreach (Curve c in profile)
                floorProfile.Append(c);
            Floor floor = Floor.Create(context.Document, new[] { floorProfile }, floorType.Id, level.Id, structural, null, 0.0);
            data["element_id"] = floor.Id.Value;
            data["element_ids"] = new[] { floor.Id.Value };
            return data;
        }

        /// <summary>
        /// 创建房间（基于 Revit 的房间边界放置）。
        /// Create a room (placed within Revit room-bounded areas).
        /// </summary>
        public static Dictionary<string, object> CreateRoom(PlanStep step, PlanExecutionContext context)
        {
            Level level = RevitLookups.ResolveLevel(context.Document, step.Arguments);
            XYZ point = PlanValues.Point(step.Arguments, "point");
            string name = PlanValues.String(step.Arguments, null, "name", "room_name");
            string number = PlanValues.String(step.Arguments, null, "number", "room_number");
            var data = new Dictionary<string, object>
            {
                { "level", level.Name },
                { "point", PlanValues.PointData(point) },
                { "name", name },
                { "number", number }
            };
            if (context.Preview)
            {
                return data;
            }

            Room room = context.Document.Create.NewRoom(level, new UV(point.X, point.Y));
            if (!string.IsNullOrWhiteSpace(name))
            {
                room.Name = name;
            }
            if (!string.IsNullOrWhiteSpace(number))
            {
                room.Number = number;
            }
            data["element_id"] = room.Id.Value;
            data["element_ids"] = new[] { room.Id.Value };
            data["name"] = room.Name;
            data["number"] = room.Number;
            return data;
        }

        /// <summary>
        /// 创建空间（MEP 空间，类似于房间但用于暖通分析）。
        /// Create a space (MEP space, similar to room but for HVAC analysis).
        /// </summary>
        public static Dictionary<string, object> CreateSpace(PlanStep step, PlanExecutionContext context)
        {
            Level level = RevitLookups.ResolveLevel(context.Document, step.Arguments);
            XYZ point = PlanValues.Point(step.Arguments, "point");
            string name = PlanValues.String(step.Arguments, null, "name", "space_name");
            string number = PlanValues.String(step.Arguments, null, "number", "space_number");
            var data = new Dictionary<string, object>
            {
                { "level", level.Name },
                { "point", PlanValues.PointData(point) },
                { "name", name },
                { "number", number }
            };
            if (context.Preview)
            {
                return data;
            }

            Space space = context.Document.Create.NewSpace(level, new UV(point.X, point.Y));
            if (!string.IsNullOrWhiteSpace(name))
            {
                space.Name = name;
            }
            if (!string.IsNullOrWhiteSpace(number))
            {
                space.Number = number;
            }
            data["element_id"] = space.Id.Value;
            data["element_ids"] = new[] { space.Id.Value };
            data["name"] = space.Name;
            data["number"] = space.Number;
            return data;
        }

        /// <summary>
        /// 创建模型线。自动计算草图平面方向。
        /// Create a model curve. Auto-computes the sketch plane orientation.
        /// </summary>
        public static Dictionary<string, object> CreateModelCurve(PlanStep step, PlanExecutionContext context)
        {
            XYZ start = PlanValues.Point(step.Arguments, "start");
            XYZ end = PlanValues.Point(step.Arguments, "end");
            if (start.DistanceTo(end) < 1e-8)
            {
                throw new BridgeCommandException("模型线 start 与 end 不能重合。");
            }
            string name = PlanValues.String(step.Arguments, null, "name");
            var data = new Dictionary<string, object>
            {
                { "start", PlanValues.PointData(start) },
                { "end", PlanValues.PointData(end) },
                { "name", name }
            };
            if (context.Preview)
            {
                return data;
            }

            // 计算法向量以创建草图平面：优先使用 Z 轴，平行时改用 X 轴
            XYZ direction = (end - start).Normalize();
            XYZ seed = Math.Abs(direction.DotProduct(XYZ.BasisZ)) < 0.9 ? XYZ.BasisZ : XYZ.BasisX;
            XYZ normal = direction.CrossProduct(seed).Normalize();
            SketchPlane sketchPlane = SketchPlane.Create(
                context.Document,
                Plane.CreateByNormalAndOrigin(normal, start));
            ModelCurve curve = context.Document.Create.NewModelCurve(
                Line.CreateBound(start, end),
                sketchPlane);
            data["element_id"] = curve.Id.Value;
            data["element_ids"] = new[] { curve.Id.Value };
            if (!string.IsNullOrWhiteSpace(name))
            {
                // 模型线在 Revit API 中不支持直接设置名称，仅记录尝试结果
                data["name_applied"] = false;
            }
            return data;
        }

        /// <summary>
        /// 创建视图（3D、平面、天花板、结构平面）。支持相机方位设置。
        /// Create a view (3D, floor plan, ceiling plan, structural plan). Supports camera orientation.
        /// </summary>
        public static Dictionary<string, object> CreateView(PlanStep step, PlanExecutionContext context)
        {
            string kind = PlanValues.String(step.Arguments, "3d", "kind", "view_kind", "view_type")
                .Trim()
                .ToLowerInvariant()
                .Replace("-", "_")
                .Replace(" ", "_");
            ViewFamily family;
            // 将 kind 字符串映射到 ViewFamily 枚举
            switch (kind)
            {
                case "3d":
                case "three_dimensional":
                case "three_d":
                    family = ViewFamily.ThreeDimensional;
                    break;
                case "floor_plan":
                case "plan":
                    family = ViewFamily.FloorPlan;
                    break;
                case "ceiling_plan":
                    family = ViewFamily.CeilingPlan;
                    break;
                case "structural_plan":
                    family = ViewFamily.StructuralPlan;
                    break;
                default:
                    throw new BridgeCommandException("create_view.kind 仅支持 3d、floor_plan、ceiling_plan、structural_plan。");
            }
            ViewFamilyType type = ResolveViewFamilyType(context.Document, step.Arguments, family);
            string name = PlanValues.String(step.Arguments, null, "name", "view_name");
            bool perspective = PlanValues.Boolean(step.Arguments, false, "perspective");
            Level level = family == ViewFamily.ThreeDimensional
                ? null
                : RevitLookups.ResolveLevel(context.Document, step.Arguments);
            XYZ eye = null;
            XYZ forward = null;
            XYZ up = null;
            // 3D 视图可读取相机参数：eye（视点）、forward（视线方向）、up（上方向）
            if (family == ViewFamily.ThreeDimensional && PlanValues.Get(step.Arguments, "eye", "eye_position", "camera_position") != null)
            {
                eye = ReadOptionalPoint(step.Arguments, "eye", "eye_position", "camera_position", "position");
                forward = ReadDirectionVector(step.Arguments, "forward", "forward_direction");
                up = PlanValues.Get(step.Arguments, "up", "up_direction") == null
                    ? XYZ.BasisZ
                    : ReadDirectionVector(step.Arguments, "up", "up_direction");
                if (Math.Abs(forward.DotProduct(up)) > 1e-3)
                {
                    throw new BridgeCommandException("create_view 的 forward 与 up 必须近似垂直。");
                }
            }
            var data = new Dictionary<string, object>
            {
                { "kind", kind },
                { "view_family", family.ToString() },
                { "type", type.Name },
                { "type_id", type.Id.Value },
                { "level", level == null ? null : level.Name },
                { "perspective", perspective },
                { "name", name }
            };
            if (eye != null)
            {
                data["eye"] = PlanValues.PointData(eye);
                data["forward"] = PlanValues.PointData(forward);
                data["up"] = PlanValues.PointData(up);
            }
            if (context.Preview)
            {
                return data;
            }

            View view;
            if (family == ViewFamily.ThreeDimensional)
            {
                view = perspective
                    ? (View)View3D.CreatePerspective(context.Document, type.Id)
                    : View3D.CreateIsometric(context.Document, type.Id);
            }
            else
            {
                if (perspective)
                {
                    throw new BridgeCommandException("perspective=true 仅适用于 3d 视图。");
                }
                view = ViewPlan.Create(context.Document, type.Id, level.Id);
            }
            if (!string.IsNullOrWhiteSpace(name))
            {
                view.Name = name;
            }
            if (eye != null)
            {
                View3D view3D = view as View3D;
                if (view3D == null)
                {
                    throw new BridgeCommandException("相机参数仅适用于 3d 视图。");
                }
                view3D.SetOrientation(new ViewOrientation3D(eye, forward, up));
            }
            data["element_id"] = view.Id.Value;
            data["element_ids"] = new[] { view.Id.Value };
            data["name"] = view.Name;
            return data;
        }

        /// <summary>
        /// 创建图纸。可指定图框类型和编号/名称。
        /// Create a sheet. Optionally specify a title block type and sheet number/name.
        /// </summary>
        public static Dictionary<string, object> CreateSheet(PlanStep step, PlanExecutionContext context)
        {
            FamilySymbol titleBlock = ResolveOptionalTitleBlock(context.Document, step.Arguments);
            string sheetNumber = PlanValues.String(step.Arguments, null, "sheet_number", "number");
            string name = PlanValues.String(step.Arguments, null, "name", "sheet_name");
            var data = new Dictionary<string, object>
            {
                { "title_block_type_id", titleBlock == null ? (object)null : titleBlock.Id.Value },
                { "title_block", titleBlock == null ? null : titleBlock.Name },
                { "sheet_number", sheetNumber },
                { "name", name }
            };
            if (context.Preview)
            {
                return data;
            }

            ViewSheet sheet = ViewSheet.Create(
                context.Document,
                titleBlock == null ? ElementId.InvalidElementId : titleBlock.Id);
            if (!string.IsNullOrWhiteSpace(sheetNumber))
            {
                sheet.SheetNumber = sheetNumber;
            }
            if (!string.IsNullOrWhiteSpace(name))
            {
                sheet.Name = name;
            }
            data["element_id"] = sheet.Id.Value;
            data["element_ids"] = new[] { sheet.Id.Value };
            data["sheet_number"] = sheet.SheetNumber;
            data["name"] = sheet.Name;
            return data;
        }

        /// <summary>
        /// 将视图放置到图纸上。校验视图是否可放置。
        /// Place a view onto a sheet. Validates whether the view can be placed.
        /// </summary>
        public static Dictionary<string, object> PlaceViewOnSheet(PlanStep step, PlanExecutionContext context)
        {
            ElementId sheetId = context.ResolveSingleElementId(step.Arguments, "sheet_id", "sheet", "target_sheet");
            ElementId viewId = context.ResolveSingleElementId(step.Arguments, "view_id", "view", "target_view");
            if (sheetId.Value == ElementId.InvalidElementId.Value ||
                viewId.Value == ElementId.InvalidElementId.Value)
            {
                return new Dictionary<string, object>
                {
                    { "deferred", true },
                    { "reason", "preview 中前置图纸或视图引用尚无真实 ID。" }
                };
            }
            ViewSheet sheet = context.Document.GetElement(sheetId) as ViewSheet;
            View view = context.Document.GetElement(viewId) as View;
            if (sheet == null)
            {
                throw new BridgeCommandException("sheet_id 不是有效图纸：" + sheetId.Value);
            }
            if (view == null || view.IsTemplate)
            {
                throw new BridgeCommandException("view_id 不是可放置的视图：" + viewId.Value);
            }
            XYZ point = PlanValues.Point(step.Arguments, "point");
            var data = new Dictionary<string, object>
            {
                { "sheet_id", sheetId.Value },
                { "view_id", viewId.Value },
                { "point", PlanValues.PointData(point) },
                { "can_place", Viewport.CanAddViewToSheet(context.Document, sheetId, viewId) }
            };
            if (!(bool)data["can_place"])
            {
                throw new BridgeCommandException("该视图不能放置到指定图纸；它可能已被放置，或属于明细表/图例等特殊视图。");
            }
            if (context.Preview)
            {
                return data;
            }

            Viewport viewport = Viewport.Create(context.Document, sheetId, viewId, point);
            data["element_id"] = viewport.Id.Value;
            data["element_ids"] = new[] { viewport.Id.Value };
            return data;
        }

        /// <summary>
        /// 创建洞口（墙洞、竖直楼板洞、竖井洞）。
        /// Create an opening (wall opening, vertical floor opening, shaft opening).
        /// </summary>
        public static Dictionary<string, object> CreateOpening(PlanStep step, PlanExecutionContext context)
        {
            string kind = PlanValues.String(step.Arguments, "wall", "kind", "opening_type").Trim().ToLowerInvariant();
            if (kind != "wall" && kind != "vertical" && kind != "shaft")
            {
                throw new BridgeCommandException("create_opening.kind 仅支持 wall、vertical、shaft。");
            }
            if (kind == "shaft")
            {
                return CreateShaftOpening(step, context);
            }

            ElementId hostId = context.ResolveSingleElementId(step.Arguments, "host_id", "host", "wall_id", "wall");
            if (hostId.Value == ElementId.InvalidElementId.Value)
            {
                return new Dictionary<string, object>
                {
                    { "deferred", true },
                    { "reason", "preview 中前置墙体引用尚无真实 ID。" }
                };
            }
            Element host = RequireElement(context.Document, hostId, "host_id");
            var data = new Dictionary<string, object>
            {
                { "kind", kind },
                { "host_id", hostId.Value }
            };
            if (kind == "wall")
            {
                Wall wall = host as Wall;
                if (wall == null)
                {
                    throw new BridgeCommandException("create_opening(kind=wall) 的 host_id 必须指向墙体。");
                }
                XYZ start = PlanValues.Point(step.Arguments, "start");
                XYZ end = PlanValues.Point(step.Arguments, "end");
                if (start.DistanceTo(end) < 1e-8)
                {
                    throw new BridgeCommandException("洞口 start 与 end 不能重合。");
                }
                data["start"] = PlanValues.PointData(start);
                data["end"] = PlanValues.PointData(end);
                if (context.Preview)
                {
                    return data;
                }
                Opening opening = context.Document.Create.NewOpening(wall, start, end);
                data["element_id"] = opening.Id.Value;
                data["element_ids"] = new[] { opening.Id.Value };
                return data;
            }

            Floor floor = host as Floor;
            if (floor == null)
            {
                throw new BridgeCommandException("create_opening(kind=vertical) 的 host_id 必须指向楼板。");
            }
            // 兼容 corner_1/corner_2 或 start/end 参数名
            XYZ corner1 = PlanValues.Point(step.Arguments,
                PlanValues.Get(step.Arguments, "corner_1") != null ? "corner_1" : "start");
            XYZ corner2 = PlanValues.Point(step.Arguments,
                PlanValues.Get(step.Arguments, "corner_2") != null ? "corner_2" : "end");
            if (Math.Abs(corner1.X - corner2.X) < 1e-8 || Math.Abs(corner1.Y - corner2.Y) < 1e-8)
            {
                throw new BridgeCommandException("竖直洞口 corner_1 与 corner_2 的 X、Y 分量均不能相同。");
            }
            data["corner_1"] = PlanValues.PointData(corner1);
            data["corner_2"] = PlanValues.PointData(corner2);
            if (context.Preview)
            {
                return data;
            }
            // Revit 2026: Opening.CreateVertical  —  使用 NewOpening + CurveLoop 创建垂直洞口
            CurveLoop verticalProfile = new CurveLoop();
            verticalProfile.Append(Line.CreateBound(corner1, new XYZ(corner2.X, corner1.Y, 0)));
            verticalProfile.Append(Line.CreateBound(new XYZ(corner2.X, corner1.Y, 0), corner2));
            verticalProfile.Append(Line.CreateBound(corner2, new XYZ(corner1.X, corner2.Y, 0)));
            verticalProfile.Append(Line.CreateBound(new XYZ(corner1.X, corner2.Y, 0), corner1));
            Element floorElement = context.Document.GetElement(floor.Id);
            CurveArray openingCurves = new CurveArray();
            foreach (Curve c in verticalProfile)
                openingCurves.Append(c);
            Opening verticalOpening = context.Document.Create.NewOpening(floorElement, openingCurves, false);
            data["element_id"] = verticalOpening.Id.Value;
            data["element_ids"] = new[] { verticalOpening.Id.Value };
            return data;
        }

        /// <summary>
        /// 创建竖井洞口（从底部标高到顶部标高）。
        /// Create a shaft opening spanning from bottom level to top level.
        /// </summary>
        private static Dictionary<string, object> CreateShaftOpening(PlanStep step, PlanExecutionContext context)
        {
            Level bottomLevel = ResolveLevelField(context.Document, step.Arguments, "bottom_level");
            Level topLevel = ResolveLevelField(context.Document, step.Arguments, "top_level");
            if (topLevel.Elevation <= bottomLevel.Elevation)
            {
                throw new BridgeCommandException("竖井 top_level 标高必须高于 bottom_level。");
            }
            CurveLoop profile = BuildBoundaryLoop(step.Arguments, "create_opening.boundary");
            var data = new Dictionary<string, object>
            {
                { "kind", "shaft" },
                { "bottom_level", bottomLevel.Name },
                { "top_level", topLevel.Name },
                { "bottom_elevation_mm", PlanValues.ToMillimeters(bottomLevel.Elevation) },
                { "top_elevation_mm", PlanValues.ToMillimeters(topLevel.Elevation) },
                { "vertex_count", profile.NumberOfCurves() }
            };
            if (context.Preview)
            {
                return data;
            }
            CurveArray shaftCurves = new CurveArray();
            foreach (Curve c in profile)
                shaftCurves.Append(c);
            Opening shaft = context.Document.Create.NewOpening(context.Document.GetElement(bottomLevel.Id), shaftCurves, false);
            data["element_id"] = shaft.Id.Value;
            data["element_ids"] = new[] { shaft.Id.Value };
            return data;
        }

        /// <summary>
        /// 从参数字段解析标高（按 ID 或名称）。
        /// Resolve a level from arguments by ID or name field.
        /// </summary>
        private static Level ResolveLevelField(Document document, IDictionary<string, object> arguments, string fieldName)
        {
            object idValue = PlanValues.Get(arguments, fieldName + "_id");
            object nameValue = PlanValues.Get(arguments, fieldName);
            if (idValue == null && nameValue == null)
            {
                throw new BridgeCommandException("缺少参数：" + fieldName + " / " + fieldName + "_id。");
            }
            var lookup = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (idValue != null)
            {
                lookup["level_id"] = idValue;
            }
            if (nameValue != null)
            {
                lookup["level"] = nameValue;
            }
            return RevitLookups.ResolveLevel(document, lookup);
        }

        /// <summary>
        /// 从点列表构建闭合边界环（用于竖井等）。
        /// Build a closed boundary loop from a list of points (for shaft openings, etc.).
        /// </summary>
        private static CurveLoop BuildBoundaryLoop(IDictionary<string, object> arguments, string fieldName)
        {
            object raw = PlanValues.Get(arguments, "boundary", "profile", "points");
            List<Dictionary<string, object>> points = PlanValues.DictionaryList(raw, fieldName);
            if (points.Count < 3)
            {
                throw new BridgeCommandException(fieldName + " 至少需要 3 个点。");
            }
            var resolved = new List<XYZ>();
            foreach (Dictionary<string, object> point in points)
            {
                double x = PlanValues.RequireMillimeters(point, "x", "x_mm");
                double y = PlanValues.RequireMillimeters(point, "y", "y_mm");
                double zOffset = PlanValues.Millimeters(point, 0.0, "z", "z_mm");
                resolved.Add(new XYZ(PlanValues.ToFeet(x), PlanValues.ToFeet(y), PlanValues.ToFeet(zOffset)));
            }
            double z = resolved[0].Z;
            if (resolved.Any(point => Math.Abs(point.Z - z) > 1e-8))
            {
                throw new BridgeCommandException(fieldName + " 必须共面且水平。");
            }
            var loop = new CurveLoop();
            for (int index = 0; index < resolved.Count; index++)
            {
                XYZ start = resolved[index];
                XYZ end = resolved[(index + 1) % resolved.Count];
                if (start.DistanceTo(end) < 1e-8)
                {
                    throw new BridgeCommandException(fieldName + " 不能包含重合的相邻点。");
                }
                loop.Append(Line.CreateBound(start, end));
            }
            return loop;
        }

        /// <summary>
        /// 构建闭合轮廓线集（用于楼板等），基于指定基础标高。
        /// Build a closed CurveArray profile for floors etc., offset from a base elevation.
        /// </summary>
        private static CurveArray BuildClosedProfile(
            IDictionary<string, object> arguments,
            double baseElevation,
            string operation)
        {
            object raw = PlanValues.Get(arguments, "boundary", "profile", "points");
            List<Dictionary<string, object>> points = PlanValues.DictionaryList(raw, operation + ".boundary");
            if (points.Count < 3)
            {
                throw new BridgeCommandException(operation + ".boundary 至少需要 3 个点。");
            }
            var resolved = new List<XYZ>();
            foreach (Dictionary<string, object> point in points)
            {
                double x = PlanValues.RequireMillimeters(point, "x", "x_mm");
                double y = PlanValues.RequireMillimeters(point, "y", "y_mm");
                double zOffset = PlanValues.Millimeters(point, 0.0, "z", "z_mm");
                resolved.Add(new XYZ(
                    PlanValues.ToFeet(x),
                    PlanValues.ToFeet(y),
                    baseElevation + PlanValues.ToFeet(zOffset)));
            }
            double z = resolved[0].Z;
            if (resolved.Any(point => Math.Abs(point.Z - z) > 1e-8))
            {
                throw new BridgeCommandException(operation + ".boundary 必须共面且水平。");
            }
            var profile = new CurveArray();
            for (int index = 0; index < resolved.Count; index++)
            {
                XYZ start = resolved[index];
                XYZ end = resolved[(index + 1) % resolved.Count];
                if (start.DistanceTo(end) < 1e-8)
                {
                    throw new BridgeCommandException(operation + ".boundary 不能包含重合的相邻点。");
                }
                profile.Append(Line.CreateBound(start, end));
            }
            return profile;
        }

        /// <summary>
        /// 解析视图族类型（ViewFamilyType），按 ID 或名称查找。
        /// Resolve a ViewFamilyType by ID or name, matching the expected view family.
        /// </summary>
        private static ViewFamilyType ResolveViewFamilyType(
            Document document,
            IDictionary<string, object> arguments,
            ViewFamily expectedFamily)
        {
            object typeId = PlanValues.Get(arguments, "type_id", "view_type_id");
            if (typeId != null)
            {
                ViewFamilyType byId = document.GetElement(
                    new ElementId(RevitLookups.ParsePositiveId(typeId, "view_type_id"))) as ViewFamilyType;
                if (byId == null || byId.ViewFamily != expectedFamily)
                {
                    throw new BridgeCommandException("view_type_id 不是 " + expectedFamily + " 视图类型。");
                }
                return byId;
            }
            string requestedName = PlanValues.String(arguments, null, "type", "type_name", "view_type_name");
            List<ViewFamilyType> candidates = new FilteredElementCollector(document)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .Where(candidate => candidate.ViewFamily == expectedFamily)
                .OrderBy(candidate => candidate.Name)
                .ToList();
            if (!string.IsNullOrWhiteSpace(requestedName))
            {
                candidates = candidates.Where(candidate =>
                    string.Equals(candidate.Name, requestedName, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            if (candidates.Count == 0)
            {
                throw new BridgeCommandException("当前项目没有可用 " + expectedFamily + " 视图类型。");
            }
            return candidates[0];
        }

        /// <summary>
        /// 解析可选的图框族符号。如未指定任何参数则返回 null。
        /// Resolve an optional title block family symbol. Returns null if no arguments specified.
        /// </summary>
        private static FamilySymbol ResolveOptionalTitleBlock(
            Document document,
            IDictionary<string, object> arguments)
        {
            object rawId = PlanValues.Get(arguments, "title_block_type_id", "titleblock_type_id");
            string family = PlanValues.String(arguments, null, "title_block_family", "titleblock_family");
            string type = PlanValues.String(arguments, null, "title_block_type", "titleblock_type");
            if (rawId == null && string.IsNullOrWhiteSpace(family) && string.IsNullOrWhiteSpace(type))
            {
                return null;
            }
            var lookup = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (rawId != null)
            {
                lookup["type_id"] = rawId;
            }
            if (!string.IsNullOrWhiteSpace(family))
            {
                lookup["family"] = family;
            }
            if (!string.IsNullOrWhiteSpace(type))
            {
                lookup["type"] = type;
            }
            FamilySymbol symbol = RevitLookups.ResolveFamilySymbol(document, lookup);
            if (symbol.Category == null ||
                symbol.Category.Id.Value != new ElementId(BuiltInCategory.OST_TitleBlocks).Value)
            {
                throw new BridgeCommandException("指定的 title_block 不是图框族类型。");
            }
            return symbol;
        }

        /// <summary>
        /// 创建 DirectShape（直接形状），支持材质和几何描述。
        /// Create a DirectShape element with optional material and geometry description.
        /// </summary>
        public static Dictionary<string, object> CreateDirectShape(PlanStep step, PlanExecutionContext context)
        {
            ElementId categoryId = RevitLookups.ResolveCategoryId(
                context.Document, step.Arguments, BuiltInCategory.OST_GenericModel);
            if (!DirectShape.IsValidCategoryId(categoryId, context.Document))
            {
                throw new BridgeCommandException("DirectShape 不支持类别 ID：" + categoryId.Value);
            }

            string name = PlanValues.String(step.Arguments, step.Id, "name");
            string materialName = PlanValues.String(step.Arguments, null, "material", "material_name");
            bool createMaterial = PlanValues.Boolean(step.Arguments, false, "create_material_if_missing");
            Material material = RevitLookups.FindMaterial(context.Document, materialName);
            if (!string.IsNullOrWhiteSpace(materialName) && material == null && !createMaterial)
            {
                throw new BridgeCommandException("找不到材质\u201c" + materialName + "\u201d。设置 create_material_if_missing=true 可在执行时创建。");
            }
            Dictionary<string, object> geometryData = RevitGeometryFactory.DescribeGeometry(step.Arguments);
            var data = new Dictionary<string, object>
            {
                { "name", name },
                { "category_id", categoryId.Value },
                { "material", materialName },
                { "material_exists", material != null },
                { "geometry", geometryData }
            };
            if (context.Preview)
            {
                return data;
            }

            if (material == null && !string.IsNullOrWhiteSpace(materialName))
            {
                ElementId materialId = Material.Create(context.Document, materialName);
                material = context.Document.GetElement(materialId) as Material;
            }
            var options = new SolidOptions(
                material == null ? ElementId.InvalidElementId : material.Id,
                ElementId.InvalidElementId);
            IList<GeometryObject> geometry = RevitGeometryFactory.CreateGeometry(step.Arguments, options);
            DirectShape shape = DirectShape.CreateElement(context.Document, categoryId);
            if (!shape.IsValidShape(geometry))
            {
                throw new BridgeCommandException("生成的 DirectShape 几何无效，未写入模型。");
            }
            shape.ApplicationId = BridgeProtocol.Version;
            shape.ApplicationDataId = PlanValues.String(step.Arguments, step.Id, "application_data_id");
            shape.Name = name;
            shape.SetShape(geometry);
            data["element_id"] = shape.Id.Value;
            data["element_ids"] = new[] { shape.Id.Value };
            return data;
        }

        /// <summary>
        /// 创建 MEP 曲线（管道、风管、线管、桥架）。支持坡度和尺寸设置。
        /// Create MEP curves (pipe, duct, conduit, cable tray). Supports slope and sizing.
        /// </summary>
        public static Dictionary<string, object> CreateMepCurve(PlanStep step, PlanExecutionContext context)
        {
            string kind = PlanValues.String(step.Arguments, null, "kind", "system");
            if (string.IsNullOrWhiteSpace(kind))
            {
                throw new BridgeCommandException("create_mep_curve 缺少 kind（pipe、duct、conduit、cable_tray）。");
            }
            XYZ start = PlanValues.Point(step.Arguments, "start");
            XYZ end = PlanValues.Point(step.Arguments, "end");
            if (start.DistanceTo(end) < 1e-8)
            {
                throw new BridgeCommandException("机电曲线 start 与 end 不能重合。");
            }
            // 坡度计算：根据起点终点水平距离调整终点 Z 值
            object rawSlope = PlanValues.Get(step.Arguments, "slope", "slope_percent", "slope_permille");
            if (rawSlope != null)
            {
                string slopeUnit = PlanValues.String(step.Arguments, "percent", "slope_unit").ToLowerInvariant();
                double slopeValue = PlanValues.Number(step.Arguments, 0.0, "slope", "slope_percent", "slope_permille");
                double slopePercent = slopeUnit == "permille" ? slopeValue / 10.0 : slopeValue;
                if (Math.Abs(slopePercent) > 100.0)
                {
                    throw new BridgeCommandException("slope 超出合理范围（-100% ~ 100%）。");
                }
                double horizontal = new XYZ(start.X, start.Y, 0).DistanceTo(new XYZ(end.X, end.Y, 0));
                if (horizontal < 1e-6)
                {
                    throw new BridgeCommandException("slope 不能用于竖直管线（start/end 水平投影重合）。");
                }
                end = new XYZ(end.X, end.Y, start.Z + horizontal * slopePercent / 100.0);
            }
            Level level = RevitLookups.ResolveLevel(context.Document, step.Arguments);
            string normalizedKind = kind.Trim().ToLowerInvariant();
            var data = new Dictionary<string, object>
            {
                { "kind", normalizedKind },
                { "level", level.Name },
                { "start", PlanValues.PointData(start) },
                { "end", PlanValues.PointData(end) }
            };
            if (rawSlope != null)
            {
                data["slope_percent"] = PlanValues.String(step.Arguments, "percent", "slope_unit").ToLowerInvariant() == "permille"
                    ? PlanValues.Number(step.Arguments, 0.0, "slope", "slope_percent", "slope_permille") / 10.0
                    : PlanValues.Number(step.Arguments, 0.0, "slope", "slope_percent", "slope_permille");
            }

            Element created;
            // 根据 kind 分发到不同的 MEP 曲线创建逻辑
            switch (normalizedKind)
            {
                case "pipe":
                    PipeType pipeType = (PipeType)RevitLookups.ResolveElementType(context.Document, typeof(PipeType), step.Arguments, true);
                    PipingSystemType pipingSystem = ResolveSystemType<PipingSystemType>(context.Document, step.Arguments, "system_type");
                    data["type"] = pipeType.Name;
                    data["system_type"] = pipingSystem.Name;
                    if (context.Preview)
                    {
                        AddMepSizeDescription(data, step.Arguments, true, false);
                        return data;
                    }
                    created = Pipe.Create(context.Document, pipingSystem.Id, pipeType.Id, level.Id, start, end);
                    SetMepSize(created, step.Arguments, true, false);
                    break;
                case "duct":
                    DuctType ductType = (DuctType)RevitLookups.ResolveElementType(context.Document, typeof(DuctType), step.Arguments, true);
                    MechanicalSystemType mechanicalSystem = ResolveSystemType<MechanicalSystemType>(context.Document, step.Arguments, "system_type");
                    data["type"] = ductType.Name;
                    data["system_type"] = mechanicalSystem.Name;
                    if (context.Preview)
                    {
                        AddMepSizeDescription(data, step.Arguments, true, true);
                        return data;
                    }
                    created = Duct.Create(context.Document, mechanicalSystem.Id, ductType.Id, level.Id, start, end);
                    SetMepSize(created, step.Arguments, true, true);
                    break;
                case "conduit":
                    ConduitType conduitType = (ConduitType)RevitLookups.ResolveElementType(context.Document, typeof(ConduitType), step.Arguments, true);
                    data["type"] = conduitType.Name;
                    if (context.Preview)
                    {
                        AddMepSizeDescription(data, step.Arguments, true, false);
                        return data;
                    }
                    created = Conduit.Create(context.Document, conduitType.Id, start, end, level.Id);
                    if (PlanValues.Get(step.Arguments, "diameter_mm", "diameter") != null &&
                        !SetOptionalLengthParameter(created, BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM, step.Arguments, "diameter_mm", "diameter"))
                    {
                        throw new BridgeCommandException("创建的线管不支持设置直径参数。");
                    }
                    break;
                case "cable_tray":
                case "cabletray":
                    CableTrayType trayType = (CableTrayType)RevitLookups.ResolveElementType(context.Document, typeof(CableTrayType), step.Arguments, true);
                    data["type"] = trayType.Name;
                    if (context.Preview)
                    {
                        AddMepSizeDescription(data, step.Arguments, false, true);
                        return data;
                    }
                    created = CableTray.Create(context.Document, trayType.Id, start, end, level.Id);
                    if (PlanValues.Get(step.Arguments, "width_mm", "width") != null &&
                        !SetOptionalLengthParameter(created, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM, step.Arguments, "width_mm", "width"))
                    {
                        throw new BridgeCommandException("创建的桥架不支持设置宽度参数。");
                    }
                    if (PlanValues.Get(step.Arguments, "height_mm", "height") != null &&
                        !SetOptionalLengthParameter(created, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM, step.Arguments, "height_mm", "height"))
                    {
                        throw new BridgeCommandException("创建的桥架不支持设置高度参数。");
                    }
                    break;
                default:
                    throw new BridgeCommandException("create_mep_curve.kind 仅支持 pipe、duct、conduit、cable_tray。");
            }

            data["element_id"] = created.Id.Value;
            data["element_ids"] = new[] { created.Id.Value };
            return data;
        }

        /// <summary>
        /// 连接两个 MEP 元素（管道、风管等），支持多种连接件类型。
        /// Connect two MEP elements (pipes, ducts, etc.) with various fitting types.
        /// </summary>
        public static Dictionary<string, object> ConnectMep(PlanStep step, PlanExecutionContext context)
        {
            ElementId aId = context.ResolveSingleElementId(step.Arguments, "element_a", "from", "first");
            ElementId bId = context.ResolveSingleElementId(step.Arguments, "element_b", "to", "second");
            string fitting = PlanValues.String(step.Arguments, "auto", "fitting").ToLowerInvariant();
            if (aId.Value == ElementId.InvalidElementId.Value ||
                bId.Value == ElementId.InvalidElementId.Value)
            {
                return new Dictionary<string, object> { { "deferred", true }, { "reason", "preview 中前置元素引用尚无真实 ID。" } };
            }
            Element first = RequireElement(context.Document, aId, "element_a");
            Element second = RequireElement(context.Document, bId, "element_b");
            Connector firstConnector = FindConnector(first, step.Arguments, "connector_a_index", second);
            Connector secondConnector = FindConnector(second, step.Arguments, "connector_b_index", first);
            var data = new Dictionary<string, object>
            {
                { "element_a", aId.Value },
                { "element_b", bId.Value },
                { "fitting", fitting },
                { "connector_a_origin", PlanValues.PointData(firstConnector.Origin) },
                { "connector_b_origin", PlanValues.PointData(secondConnector.Origin) }
            };
            if (context.Preview)
            {
                return data;
            }

            // 可选：延伸管线至交点后再连接
            if (PlanValues.Boolean(step.Arguments, false, "extend_to_intersection", "extend"))
            {
                XYZ intersection = ExtendMepCurvesToIntersection(context.Document, first, second);
                firstConnector = FindConnector(first, step.Arguments, "connector_a_index", second);
                secondConnector = FindConnector(second, step.Arguments, "connector_b_index", first);
                data["connector_a_origin"] = PlanValues.PointData(firstConnector.Origin);
                data["connector_b_origin"] = PlanValues.PointData(secondConnector.Origin);
                data["intersection"] = PlanValues.PointData(intersection);
                data["extend_to_intersection"] = true;
            }

            Element fittingElement = null;
            // 根据 fitting 类型创建对应连接件
            switch (fitting)
            {
                case "auto":
                case "direct":
                    firstConnector.ConnectTo(secondConnector);
                    break;
                case "elbow":
                    fittingElement = context.Document.Create.NewElbowFitting(firstConnector, secondConnector);
                    break;
                case "union":
                    fittingElement = context.Document.Create.NewUnionFitting(firstConnector, secondConnector);
                    break;
                case "tee":
                    ElementId cId = context.ResolveSingleElementId(step.Arguments, "element_c", "third");
                    Element third = RequireElement(context.Document, cId, "element_c");
                    Connector thirdConnector = FindConnector(third, step.Arguments, "connector_c_index", first);
                    fittingElement = context.Document.Create.NewTeeFitting(firstConnector, secondConnector, thirdConnector);
                    data["element_c"] = cId.Value;
                    data["connector_c_origin"] = PlanValues.PointData(thirdConnector.Origin);
                    break;
                case "reducer":
                    fittingElement = context.Document.Create.NewTransitionFitting(firstConnector, secondConnector);
                    break;
                case "cross":
                    ElementId crossCId = context.ResolveSingleElementId(step.Arguments, "element_c", "third");
                    ElementId crossDId = context.ResolveSingleElementId(step.Arguments, "element_d", "fourth");
                    Element crossThird = RequireElement(context.Document, crossCId, "element_c");
                    Element crossFourth = RequireElement(context.Document, crossDId, "element_d");
                    Connector crossThirdConnector = FindConnector(crossThird, step.Arguments, "connector_c_index", first);
                    Connector crossFourthConnector = FindConnector(crossFourth, step.Arguments, "connector_d_index", second);
                    fittingElement = context.Document.Create.NewCrossFitting(
                        firstConnector, secondConnector, crossThirdConnector, crossFourthConnector);
                    data["element_c"] = crossCId.Value;
                    data["element_d"] = crossDId.Value;
                    data["connector_c_origin"] = PlanValues.PointData(crossThirdConnector.Origin);
                    data["connector_d_origin"] = PlanValues.PointData(crossFourthConnector.Origin);
                    break;
                default:
                    throw new BridgeCommandException("connect_mep.fitting 仅支持 auto、direct、elbow、union、tee、reducer、cross。");
            }
            if (fittingElement != null)
            {
                data["element_id"] = fittingElement.Id.Value;
                data["element_ids"] = new[] { fittingElement.Id.Value };
            }
            return data;
        }

        /// <summary>
        /// 创建 MEP 系统（管道系统或机械系统），并可添加成员。
        /// Create an MEP system (piping or mechanical) and optionally add members.
        /// </summary>
        public static Dictionary<string, object> CreateMepSystem(PlanStep step, PlanExecutionContext context)
        {
            string domain = PlanValues.String(step.Arguments, null, "domain", "kind").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(domain))
            {
                throw new BridgeCommandException("create_mep_system 缺少 domain（piping、mechanical）。");
            }
            string name = PlanValues.String(step.Arguments, step.Id, "name", "system_name");
            var data = new Dictionary<string, object>
            {
                { "domain", domain },
                { "name", name }
            };

            ElementId systemTypeId;
            if (domain == "piping")
            {
                PipingSystemType pipingType = ResolveSystemType<PipingSystemType>(context.Document, step.Arguments, "system_type");
                systemTypeId = pipingType.Id;
                data["system_type"] = pipingType.Name;
            }
            else if (domain == "mechanical")
            {
                MechanicalSystemType mechanicalType = ResolveSystemType<MechanicalSystemType>(context.Document, step.Arguments, "system_type");
                systemTypeId = mechanicalType.Id;
                data["system_type"] = mechanicalType.Name;
            }
            else
            {
                throw new BridgeCommandException("create_mep_system.domain 仅支持 piping、mechanical。");
            }

            List<ElementId> members = new List<ElementId>();
            object rawMembers = PlanValues.Get(step.Arguments, "members", "element_ids", "elements");
            if (rawMembers != null)
            {
                foreach (ElementId memberId in context.ResolveElementIds(step.Arguments, "members", "element_ids", "elements"))
                {
                    if (memberId.Value != ElementId.InvalidElementId.Value)
                    {
                        members.Add(memberId);
                    }
                }
            }
            data["member_count"] = members.Count;
            if (context.Preview)
            {
                return data;
            }

            ElementId systemId = domain == "piping"
                ? PipingSystem.Create(context.Document, systemTypeId, name).Id
                : MechanicalSystem.Create(context.Document, systemTypeId, name).Id;
            data["element_id"] = systemId.Value;
            data["element_ids"] = new[] { systemId.Value };
            data["name"] = RevitLookups.ElementName(context.Document.GetElement(systemId));

            // 将成员逐一添加到系统中（通过连接件集）
            int added = 0;
            foreach (ElementId memberId in members)
            {
                try
                {
                    Element member = context.Document.GetElement(memberId);
                    ConnectorSet connectorSet = new ConnectorSet();
                    ConnectorManager cm = null;
                    MEPCurve mepCurve = member as MEPCurve;
                    if (mepCurve != null)
                    {
                        cm = mepCurve.ConnectorManager;
                    }
                    else
                    {
                        FamilyInstance fi = member as FamilyInstance;
                        if (fi != null && fi.MEPModel != null)
                        {
                            cm = fi.MEPModel.ConnectorManager;
                        }
                    }
                    if (cm != null)
                    {
                        foreach (Connector c in cm.Connectors)
                        {
                            connectorSet.Insert(c);
                        }
                    }
                    if (domain == "piping")
                    {
                        ((PipingSystem)context.Document.GetElement(systemId)).Add(connectorSet);
                        added++;
                    }
                    else
                    {
                        ((MechanicalSystem)context.Document.GetElement(systemId)).Add(connectorSet);
                        added++;
                    }
                }
                catch (Exception ex)
                {
                    throw new BridgeCommandException(
                        "系统成员 element_id=" + memberId.Value + " 指派失败（域或类型不匹配）：" + ex.Message);
                }
            }
            data["member_added"] = added;
            return data;
        }

        /// <summary>
        /// 加载族文件（.rfa）到当前项目，可选激活特定族类型。
        /// Load a family file (.rfa) into the current project, optionally activating a specific symbol.
        /// </summary>
        public static Dictionary<string, object> LoadFamily(PlanStep step, PlanExecutionContext context)
        {
            string path = PlanValues.String(step.Arguments, null, "path", "family_path", "file");
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new BridgeCommandException("load_family 缺少 path（.rfa 文件完整路径）。");
            }
            if (!System.IO.File.Exists(path))
            {
                throw new BridgeCommandException("load_family.path 不存在：" + path);
            }
            string symbolName = PlanValues.String(step.Arguments, null, "symbol", "type", "type_name");
            var data = new Dictionary<string, object>
            {
                { "path", path },
                { "symbol", symbolName }
            };
            if (context.Preview)
            {
                return data;
            }

            Family family;
            if (!context.Document.LoadFamily(path, new BridgeFamilyLoadOptions(), out family) || family == null)
            {
                throw new BridgeCommandException("Revit 拒绝加载族文件：" + path);
            }
            data["family"] = family.Name;
            data["element_id"] = family.Id.Value;
            data["element_ids"] = new[] { family.Id.Value };
            var symbolNames = new List<string>();
            FamilySymbol matchedSymbol = null;
            foreach (ElementId symbolId in family.GetFamilySymbolIds())
            {
                FamilySymbol symbol = context.Document.GetElement(symbolId) as FamilySymbol;
                if (symbol == null)
                {
                    continue;
                }
                symbolNames.Add(symbol.Name);
                if (matchedSymbol == null && !string.IsNullOrWhiteSpace(symbolName) &&
                    string.Equals(symbol.Name, symbolName, StringComparison.OrdinalIgnoreCase))
                {
                    matchedSymbol = symbol;
                }
            }
            data["symbol_names"] = symbolNames.ToArray();
            if (!string.IsNullOrWhiteSpace(symbolName))
            {
                if (matchedSymbol == null)
                {
                    throw new BridgeCommandException(
                        "族\u201c" + family.Name + "\u201d没有类型\u201c" + symbolName + "\u201d。可用：" + string.Join("\u3001", symbolNames));
                }
                ActivateSymbol(context.Document, matchedSymbol);
                data["symbol_id"] = matchedSymbol.Id.Value;
            }
            return data;
        }

        /// <summary>
        /// 创建管道/风管保温层。
        /// Create pipe or duct insulation.
        /// </summary>
        public static Dictionary<string, object> CreateInsulation(PlanStep step, PlanExecutionContext context)
        {
            IList<ElementId> targets = context.ResolveElementIds(step.Arguments, "element_ids", "elements", "targets");
            if (targets.Count == 0)
            {
                return new Dictionary<string, object> { { "deferred", true }, { "reason", "preview 中前置元素引用尚无真实 ID。" } };
            }
            double thicknessMm = PlanValues.RequireMillimeters(step.Arguments, "thickness_mm", "thickness");
            if (thicknessMm <= 0.0)
            {
                throw new BridgeCommandException("create_insulation.thickness_mm 必须大于 0。");
            }
            string typeName = PlanValues.String(step.Arguments, null, "type", "insulation_type");
            var data = new Dictionary<string, object>
            {
                { "target_count", targets.Count },
                { "thickness_mm", thicknessMm },
                { "type", typeName }
            };
            if (context.Preview)
            {
                return data;
            }

            double thickness = PlanValues.ToFeet(thicknessMm);
            var created = new List<ElementId>();
            bool pipeTypeResolved = false;
            bool ductTypeResolved = false;
            ElementId pipeTypeId = null;
            ElementId ductTypeId = null;
            foreach (ElementId targetId in targets)
            {
                Element target = RequireElement(context.Document, targetId, "element_ids");
                Pipe pipe = target as Pipe;
                if (pipe != null)
                {
                    if (!pipeTypeResolved)
                    {
                        pipeTypeId = ResolveInsulationTypeId<PipeInsulationType>(context.Document, typeName);
                        pipeTypeResolved = true;
                    }
                    created.Add(PipeInsulation.Create(context.Document, pipe.Id, pipeTypeId, thickness).Id);
                    continue;
                }
                Duct duct = target as Duct;
                if (duct != null)
                {
                    if (!ductTypeResolved)
                    {
                        ductTypeId = ResolveInsulationTypeId<DuctInsulationType>(context.Document, typeName);
                        ductTypeResolved = true;
                    }
                    created.Add(DuctInsulation.Create(context.Document, duct.Id, ductTypeId, thickness).Id);
                    continue;
                }
                throw new BridgeCommandException("create_insulation 目标必须是管道或风管：element_id=" + targetId.Value);
            }
            data["element_ids"] = created.Select(id => id.Value).ToArray();
            data["created_count"] = created.Count;
            if (created.Count == 1)
            {
                data["element_id"] = created[0].Value;
            }
            return data;
        }

        /// <summary>
        /// 解析保温层类型 ID（PipeInsulationType 或 DuctInsulationType）。
        /// Resolve the insulation type ID (PipeInsulationType or DuctInsulationType).
        /// </summary>
        private static ElementId ResolveInsulationTypeId<T>(Document document, string typeName) where T : ElementType
        {
            List<T> candidates = new FilteredElementCollector(document)
                .OfClass(typeof(T))
                .Cast<T>()
                .OrderBy(RevitLookups.ElementName)
                .ToList();
            if (!string.IsNullOrWhiteSpace(typeName))
            {
                candidates = candidates.Where(candidate =>
                    string.Equals(RevitLookups.ElementName(candidate), typeName, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            if (candidates.Count == 0)
            {
                throw new BridgeCommandException(
                    "当前项目没有可用 " + typeof(T).Name + (string.IsNullOrWhiteSpace(typeName) ? string.Empty : "（名称 " + typeName + "）") + "。");
            }
            return candidates[0].Id;
        }

        /// <summary>
        /// 创建放样形状（扫掠体）。支持矩形/圆形截面，可含壁厚。
        /// Create a swept solid shape. Supports rectangular/circular profiles with optional wall thickness.
        /// </summary>
        public static Dictionary<string, object> CreateSweptShape(PlanStep step, PlanExecutionContext context)
        {
            List<Dictionary<string, object>> rawPoints = PlanValues.DictionaryList(
                PlanValues.Get(step.Arguments, "path", "points"), "create_swept_shape.path");
            var pathPoints = new List<XYZ>();
            foreach (Dictionary<string, object> item in rawPoints)
            {
                pathPoints.Add(new XYZ(
                    PlanValues.ToFeet(PlanValues.RequireMillimeters(item, "x", "x_mm")),
                    PlanValues.ToFeet(PlanValues.RequireMillimeters(item, "y", "y_mm")),
                    PlanValues.ToFeet(PlanValues.Millimeters(item, 0.0, "z", "z_mm"))));
            }
            Dictionary<string, object> section = PlanValues.Dictionary(
                PlanValues.Get(step.Arguments, "section", "profile"), "create_swept_shape.section");
            string shape = PlanValues.String(section, "rect", "shape", "kind");
            double widthMm = PlanValues.RequireMillimeters(section, "width_mm", "width", "diameter_mm", "diameter");
            double heightMm = PlanValues.Millimeters(section, widthMm, "height_mm", "height");
            double wallThicknessMm = PlanValues.Millimeters(section, 0.0, "wall_thickness_mm", "wall_thickness");
            ElementId categoryId = RevitLookups.ResolveCategoryId(
                context.Document, step.Arguments, BuiltInCategory.OST_GenericModel);
            string name = PlanValues.String(step.Arguments, step.Id, "name");

            var data = new Dictionary<string, object>
            {
                { "name", name },
                { "path_point_count", pathPoints.Count },
                { "section", new Dictionary<string, object>
                    {
                        { "shape", shape },
                        { "width_mm", widthMm },
                        { "height_mm", heightMm },
                        { "wall_thickness_mm", wallThicknessMm }
                    }
                },
                { "category_id", categoryId.Value }
            };
            CurveLoop path = RevitSectionFactory.BuildPath(pathPoints, "create_swept_shape.path");
            XYZ pathStart = path.First().GetEndPoint(0);
            XYZ tangent = path.First().GetEndPoint(1).Subtract(path.First().GetEndPoint(0));
            IList<CurveLoop> profiles = RevitSectionFactory.CreateSectionLoops(
                shape, widthMm, heightMm, wallThicknessMm, pathStart, tangent);
            if (context.Preview)
            {
                data["profile_loop_count"] = profiles.Count;
                return data;
            }

            Solid solid = GeometryCreationUtilities.CreateSweptGeometry(path, 0, 0, profiles);
            IList<GeometryObject> geometry = new List<GeometryObject> { solid };
            DirectShape directShape = DirectShape.CreateElement(context.Document, categoryId);
            if (!directShape.IsValidShape(geometry))
            {
                throw new BridgeCommandException("放样几何无效（截面可能自交或与路径平面冲突）。");
            }
            directShape.ApplicationId = BridgeProtocol.Version;
            directShape.ApplicationDataId = step.Id;
            directShape.Name = name;
            directShape.SetShape(geometry);
            data["element_id"] = directShape.Id.Value;
            data["element_ids"] = new[] { directShape.Id.Value };
            return data;
        }

        /// <summary>
        /// 放置族实例。支持多种放置类型（基于标高、基于面、工作平面、视图等）。
        /// Place a family instance. Supports multiple placement types (level-based, face-hosted, work-plane, view-based, etc.).
        /// </summary>
        public static Dictionary<string, object> PlaceFamilyInstance(PlanStep step, PlanExecutionContext context)
        {
            FamilySymbol symbol = RevitLookups.ResolveFamilySymbol(context.Document, step.Arguments);
            StructuralType structuralType = ResolveStructuralType(step.Arguments, StructuralType.NonStructural);
            if (symbol.Family == null)
            {
                throw new BridgeCommandException("族类型缺少 Family 信息。");
            }
            FamilyPlacementType placementType = symbol.Family.FamilyPlacementType;
            var data = new Dictionary<string, object>
            {
                { "family", symbol.Family.Name },
                { "type", symbol.Name },
                { "type_id", symbol.Id.Value },
                { "placement_type", placementType.ToString() },
                { "structural_type", structuralType.ToString() }
            };
            FamilyInstance instance;
            switch (placementType)
            {
                case FamilyPlacementType.OneLevelBased:
                case FamilyPlacementType.TwoLevelsBased:
                    XYZ point = PlanValues.Point(step.Arguments, "point");
                    Level level = RevitLookups.ResolveLevel(context.Document, step.Arguments);
                    data["point"] = PlanValues.PointData(point);
                    data["level"] = level.Name;
                    if (context.Preview)
                    {
                        return data;
                    }
                    ActivateSymbol(context.Document, symbol);
                    instance = context.Document.Create.NewFamilyInstance(point, symbol, level, structuralType);
                    break;

                case FamilyPlacementType.OneLevelBasedHosted:
                    XYZ hostedPoint = PlanValues.Point(step.Arguments, "point");
                    ElementId hostId = context.ResolveSingleElementId(step.Arguments, "host_id", "host");
                    if (hostId.Value == ElementId.InvalidElementId.Value)
                    {
                        return DeferredPlacement(data, "$host");
                    }
                    Element host = RequireElement(context.Document, hostId, "host_id");
                    data["point"] = PlanValues.PointData(hostedPoint);
                    data["host_id"] = hostId.Value;
                    data["host_class"] = host.GetType().FullName;
                    if (context.Preview)
                    {
                        return data;
                    }
                    ActivateSymbol(context.Document, symbol);
                    instance = context.Document.Create.NewFamilyInstance(hostedPoint, symbol, host, structuralType);
                    break;

                case FamilyPlacementType.WorkPlaneBased:
                    XYZ workPlanePoint = PlanValues.Point(step.Arguments, "point");
                    data["point"] = PlanValues.PointData(workPlanePoint);
                    bool useHostFace = PlanValues.Get(step.Arguments, "host_face_index", "face_index") != null ||
                        string.Equals(PlanValues.String(step.Arguments, null, "placement_mode", "mode"), "face", StringComparison.OrdinalIgnoreCase);
                    if (useHostFace)
                    {
                        ElementId faceHostId = context.ResolveSingleElementId(step.Arguments, "host_id", "host");
                        if (faceHostId.Value == ElementId.InvalidElementId.Value)
                        {
                            return DeferredPlacement(data, "$host");
                        }
                        Element faceHost = RequireElement(context.Document, faceHostId, "host_id");
                        int faceIndex = PlanValues.Integer(step.Arguments, 0, "host_face_index", "face_index");
                        Reference face = FindFaceReference(faceHost, faceIndex);
                        XYZ direction = ReadReferenceDirection(step.Arguments);
                        data["host_id"] = faceHostId.Value;
                        data["host_face_index"] = faceIndex;
                        data["reference_direction"] = PlanValues.PointData(direction);
                        if (context.Preview)
                        {
                            return data;
                        }
                        ActivateSymbol(context.Document, symbol);
                        instance = context.Document.Create.NewFamilyInstance(face, workPlanePoint, direction, symbol);
                    }
                    else
                    {
                        if (context.Preview)
                        {
                            object existingWorkPlaneId = PlanValues.Get(step.Arguments, "work_plane_id", "sketch_plane_id");
                            data["work_plane_id"] = existingWorkPlaneId == null ? (object)null : existingWorkPlaneId;
                            data["work_plane_mode"] = existingWorkPlaneId == null ? "create" : "existing";
                            return data;
                        }
                        SketchPlane workPlane = ResolveOrCreateWorkPlane(context, step.Arguments, workPlanePoint);
                        data["work_plane_id"] = workPlane.Id.Value;
                        ActivateSymbol(context.Document, symbol);
                        instance = context.Document.Create.NewFamilyInstance(workPlanePoint, symbol, workPlane, structuralType);
                    }
                    break;

                case FamilyPlacementType.ViewBased:
                    XYZ viewPoint = PlanValues.Point(step.Arguments, "point");
                    View view = ResolveTargetView(context, step.Arguments, "view_id", "view");
                    data["point"] = PlanValues.PointData(viewPoint);
                    data["view_id"] = view.Id.Value;
                    data["view_name"] = view.Name;
                    if (context.Preview)
                    {
                        return data;
                    }
                    ActivateSymbol(context.Document, symbol);
                    instance = context.Document.Create.NewFamilyInstance(viewPoint, symbol, view);
                    break;

                case FamilyPlacementType.CurveBased:
                case FamilyPlacementType.CurveBasedDetail:
                case FamilyPlacementType.CurveDrivenStructural:
                    XYZ start = PlanValues.Point(step.Arguments, "start");
                    XYZ end = PlanValues.Point(step.Arguments, "end");
                    if (start.DistanceTo(end) < 1e-8)
                    {
                        throw new BridgeCommandException("线基族 start 与 end 不能重合。");
                    }
                    Line line = Line.CreateBound(start, end);
                    data["start"] = PlanValues.PointData(start);
                    data["end"] = PlanValues.PointData(end);
                    if (placementType == FamilyPlacementType.CurveBasedDetail ||
                        PlanValues.Get(step.Arguments, "view_id", "view") != null)
                    {
                        View curveView = ResolveTargetView(context, step.Arguments, "view_id", "view");
                        data["view_id"] = curveView.Id.Value;
                        data["view_name"] = curveView.Name;
                        if (context.Preview)
                        {
                            return data;
                        }
                        ActivateSymbol(context.Document, symbol);
                        instance = context.Document.Create.NewFamilyInstance(line, symbol, curveView);
                    }
                    else
                    {
                        Level curveLevel = RevitLookups.ResolveLevel(context.Document, step.Arguments);
                        data["level"] = curveLevel.Name;
                        if (context.Preview)
                        {
                            return data;
                        }
                        ActivateSymbol(context.Document, symbol);
                        instance = context.Document.Create.NewFamilyInstance(line, symbol, curveLevel, structuralType);
                    }
                    break;

                case FamilyPlacementType.Adaptive:
                    List<XYZ> adaptivePoints = ReadAdaptivePoints(step.Arguments);
                    data["adaptive_point_count"] = adaptivePoints.Count;
                    if (context.Preview)
                    {
                        return data;
                    }
                    ActivateSymbol(context.Document, symbol);
                    instance = AdaptiveComponentInstanceUtils.CreateAdaptiveComponentInstance(context.Document, symbol);
                    IList<ElementId> pointIds = AdaptiveComponentInstanceUtils.GetInstancePlacementPointElementRefIds(instance);
                    if (pointIds.Count != adaptivePoints.Count)
                    {
                        throw new BridgeCommandException(
                            "自适应族需要 " + pointIds.Count + " 个 adaptive_points，当前收到 " + adaptivePoints.Count + " 个。");
                    }
                    for (int index = 0; index < pointIds.Count; index++)
                    {
                        ReferencePoint referencePoint = context.Document.GetElement(pointIds[index]) as ReferencePoint;
                        if (referencePoint == null)
                        {
                            throw new BridgeCommandException("未能定位自适应族放置点：" + pointIds[index].Value);
                        }
                        referencePoint.Position = adaptivePoints[index];
                    }
                    data["adaptive_point_ids"] = pointIds.Select(id => id.Value).ToArray();
                    break;

                default:
                    throw new BridgeCommandException(
                        "族\u201c" + symbol.Family.Name + "\u201d使用暂未识别的放置类型：" + placementType + "。");
            }
            data["element_id"] = instance.Id.Value;
            data["element_ids"] = new[] { instance.Id.Value };
            return data;
        }

        /// <summary>
        /// 创建结构构件（梁、支撑、柱）。
        /// Create structural members (beam, brace, column).
        /// </summary>
        public static Dictionary<string, object> CreateStructuralMember(PlanStep step, PlanExecutionContext context)
        {
            string kind = PlanValues.String(step.Arguments, null, "kind");
            if (string.IsNullOrWhiteSpace(kind))
            {
                throw new BridgeCommandException("create_structural_member 缺少 kind（beam、brace、column）。");
            }
            FamilySymbol symbol = RevitLookups.ResolveFamilySymbol(context.Document, step.Arguments);
            Level level = RevitLookups.ResolveLevel(context.Document, step.Arguments);
            string normalizedKind = kind.Trim().ToLowerInvariant();
            StructuralType structuralType;
            var data = new Dictionary<string, object>
            {
                { "kind", normalizedKind },
                { "family", symbol.Family == null ? null : symbol.Family.Name },
                { "type", symbol.Name },
                { "type_id", symbol.Id.Value },
                { "level", level.Name }
            };

            if (normalizedKind == "beam" || normalizedKind == "brace")
            {
                XYZ start = PlanValues.Point(step.Arguments, "start");
                XYZ end = PlanValues.Point(step.Arguments, "end");
                if (start.DistanceTo(end) < 1e-8)
                {
                    throw new BridgeCommandException("结构线构件 start 与 end 不能重合。");
                }
                structuralType = normalizedKind == "beam" ? StructuralType.Beam : StructuralType.Brace;
                data["start"] = PlanValues.PointData(start);
                data["end"] = PlanValues.PointData(end);
                if (context.Preview)
                {
                    return data;
                }
                ActivateSymbol(context.Document, symbol);
                FamilyInstance lineInstance = context.Document.Create.NewFamilyInstance(
                    Line.CreateBound(start, end), symbol, level, structuralType);
                data["element_id"] = lineInstance.Id.Value;
                data["element_ids"] = new[] { lineInstance.Id.Value };
                return data;
            }

            if (normalizedKind == "column")
            {
                XYZ point = PlanValues.Point(step.Arguments, "point");
                data["point"] = PlanValues.PointData(point);
                if (context.Preview)
                {
                    return data;
                }
                ActivateSymbol(context.Document, symbol);
                FamilyInstance column = context.Document.Create.NewFamilyInstance(point, symbol, level, StructuralType.Column);
                SetOptionalLengthParameter(column, BuiltInParameter.INSTANCE_LENGTH_PARAM, step.Arguments, "height_mm", "height");
                data["element_id"] = column.Id.Value;
                data["element_ids"] = new[] { column.Id.Value };
                return data;
            }
            throw new BridgeCommandException("create_structural_member.kind 仅支持 beam、brace、column。");
        }

        /// <summary>
        /// 解析楼板类型。过滤掉基础底板。
        /// Resolve a FloorType, filtering out foundation slabs.
        /// </summary>
        private static FloorType ResolveFloorType(
            Document document,
            IDictionary<string, object> arguments)
        {
            object idValue = PlanValues.Get(arguments, "type_id", "floor_type_id");
            if (idValue != null)
            {
                FloorType byId = document.GetElement(
                    new ElementId(RevitLookups.ParsePositiveId(idValue, "floor_type_id"))) as FloorType;
                if (byId == null)
                {
                    throw new BridgeCommandException("floor_type_id 不是有效 FloorType。");
                }
                if (byId.IsFoundationSlab)
                {
                    throw new BridgeCommandException("floor_type_id 指向基础底板。create_floor 仅支持普通楼板类型。");
                }
                return byId;
            }

            string requestedName = PlanValues.String(arguments, null, "type", "type_name", "floor_type");
            List<FloorType> candidates = new FilteredElementCollector(document)
                .OfClass(typeof(FloorType))
                .Cast<FloorType>()
                .Where(candidate => !candidate.IsFoundationSlab)
                .OrderBy(candidate => candidate.Name)
                .ToList();
            if (!string.IsNullOrWhiteSpace(requestedName))
            {
                candidates = candidates.Where(candidate =>
                    string.Equals(candidate.Name, requestedName, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            if (candidates.Count == 0)
            {
                throw new BridgeCommandException(
                    "当前项目没有可用普通楼板类型。请先用 query_catalog(kind=types) 查询，或在项目中创建普通楼板类型。");
            }
            return candidates[0];
        }

        /// <summary>
        /// 解析系统类型（如 PipingSystemType / MechanicalSystemType）。
        /// Resolve a system type (e.g. PipingSystemType / MechanicalSystemType).
        /// </summary>
        private static T ResolveSystemType<T>(Document document, IDictionary<string, object> arguments, string fieldName)
            where T : ElementType
        {
            object idValue = PlanValues.Get(arguments, fieldName + "_id");
            if (idValue != null)
            {
                int id = RevitLookups.ParsePositiveId(idValue, fieldName + "_id");
                T byId = document.GetElement(new ElementId(id)) as T;
                if (byId == null)
                {
                    throw new BridgeCommandException(fieldName + "_id=" + id + " 不是 " + typeof(T).Name + "。");
                }
                return byId;
            }

            string requestedName = PlanValues.String(arguments, null, fieldName);
            List<T> candidates = new FilteredElementCollector(document)
                .OfClass(typeof(T))
                .Cast<T>()
                .OrderBy(RevitLookups.ElementName)
                .ToList();
            if (!string.IsNullOrWhiteSpace(requestedName))
            {
                candidates = candidates.Where(candidate =>
                    string.Equals(candidate.Name, requestedName, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            if (candidates.Count == 0)
            {
                throw new BridgeCommandException("当前项目没有可用 " + typeof(T).Name + "。先用 query_catalog(kind=mep_types) 查询。");
            }
            return candidates[0];
        }

        /// <summary>
        /// 在预览数据中添加 MEP 尺寸描述（直径/宽/高）。
        /// Add MEP size description (diameter/width/height) to preview data.
        /// </summary>
        private static void AddMepSizeDescription(
            IDictionary<string, object> data,
            IDictionary<string, object> arguments,
            bool allowDiameter,
            bool allowRectangular)
        {
            object diameter = PlanValues.Get(arguments, "diameter_mm", "diameter");
            object width = PlanValues.Get(arguments, "width_mm", "width");
            object height = PlanValues.Get(arguments, "height_mm", "height");
            if (diameter != null)
            {
                if (!allowDiameter)
                {
                    throw new BridgeCommandException("此 MEP 曲线不支持 diameter_mm。");
                }
                data["diameter_mm"] = PlanValues.ParseMillimeters(diameter, "diameter_mm");
            }
            if (width != null || height != null)
            {
                if (!allowRectangular)
                {
                    throw new BridgeCommandException("此 MEP 曲线不支持 width_mm/height_mm。");
                }
                if (width != null)
                {
                    data["width_mm"] = PlanValues.ParseMillimeters(width, "width_mm");
                }
                if (height != null)
                {
                    data["height_mm"] = PlanValues.ParseMillimeters(height, "height_mm");
                }
            }
        }

        /// <summary>
        /// 在已创建的 MEP 元素上设置尺寸（直径/宽/高）。
        /// Set sizing (diameter/width/height) on a created MEP element.
        /// </summary>
        private static void SetMepSize(Element element, IDictionary<string, object> arguments, bool allowDiameter, bool allowRectangular)
        {
            AddMepSizeDescription(new Dictionary<string, object>(), arguments, allowDiameter, allowRectangular);
            if (PlanValues.Get(arguments, "diameter_mm", "diameter") != null && allowDiameter)
            {
                bool pipeDiameterSet = SetOptionalLengthParameter(element, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM, arguments, "diameter_mm", "diameter");
                bool curveDiameterSet = SetOptionalLengthParameter(element, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM, arguments, "diameter_mm", "diameter");
                if (!pipeDiameterSet && !curveDiameterSet)
                {
                    throw new BridgeCommandException("创建的 MEP 曲线不支持设置直径参数。");
                }
            }
            if (allowRectangular)
            {
                if (PlanValues.Get(arguments, "width_mm", "width") != null &&
                    !SetOptionalLengthParameter(element, BuiltInParameter.RBS_CURVE_WIDTH_PARAM, arguments, "width_mm", "width"))
                {
                    throw new BridgeCommandException("创建的 MEP 曲线不支持设置宽度参数。");
                }
                if (PlanValues.Get(arguments, "height_mm", "height") != null &&
                    !SetOptionalLengthParameter(element, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM, arguments, "height_mm", "height"))
                {
                    throw new BridgeCommandException("创建的 MEP 曲线不支持设置高度参数。");
                }
            }
        }

        /// <summary>
        /// 尝试设置元素的可选长度参数（毫米值转 Revit 内部单位）。
        /// Attempt to set an optional length parameter (mm value converted to Revit internal units).
        /// </summary>
        private static bool SetOptionalLengthParameter(
            Element element,
            BuiltInParameter builtInParameter,
            IDictionary<string, object> arguments,
            params string[] fieldNames)
        {
            object raw = PlanValues.Get(arguments, fieldNames);
            if (raw == null)
            {
                return false;
            }
            double millimeters = PlanValues.ParseMillimeters(raw, string.Join("/", fieldNames));
            if (millimeters <= 0.0)
            {
                throw new BridgeCommandException("参数 " + string.Join("/", fieldNames) + " 必须大于 0。");
            }
            Parameter parameter = element.get_Parameter(builtInParameter);
            if (parameter == null || parameter.IsReadOnly)
            {
                return false;
            }
            parameter.Set(PlanValues.ToFeet(millimeters));
            return true;
        }

        /// <summary>
        /// 获取文档中指定 ID 的元素，不存在时抛出异常。
        /// Get an element by ID from the document, throwing if not found.
        /// </summary>
        private static Element RequireElement(Document document, ElementId id, string fieldName)
        {
            Element element = document.GetElement(id);
            if (element == null)
            {
                throw new BridgeCommandException("找不到 " + fieldName + "=" + id.Value + " 对应元素。");
            }
            return element;
        }

        /// <summary>
        /// 查找 MEP 元素的连接器（按索引或最近连接器）。
        /// Find a connector on an MEP element (by index or nearest to another element's connectors).
        /// </summary>
        private static Connector FindConnector(
            Element element,
            IDictionary<string, object> arguments,
            string indexName,
            Element otherElement)
        {
            List<Connector> connectors = GetConnectors(element);
            if (connectors.Count == 0)
            {
                throw new BridgeCommandException("元素 " + element.Id.Value + " 没有可连接的 MEP Connector。");
            }
            object rawIndex = PlanValues.Get(arguments, indexName);
            if (rawIndex != null)
            {
                int index = PlanValues.Integer(arguments, -1, indexName);
                connectors = connectors.OrderBy(connector => connector.Origin.X)
                    .ThenBy(connector => connector.Origin.Y)
                    .ThenBy(connector => connector.Origin.Z)
                    .ToList();
                if (index < 0 || index >= connectors.Count)
                {
                    throw new BridgeCommandException(indexName + " 超出范围 0-" + (connectors.Count - 1) + "。");
                }
                return connectors[index];
            }

            // 未指定索引时，选择距离另一元素最近的连接器
            List<Connector> otherConnectors = GetConnectors(otherElement);
            if (otherConnectors.Count == 0)
            {
                return connectors[0];
            }
            return connectors.OrderBy(connector => otherConnectors.Min(other => connector.Origin.DistanceTo(other.Origin))).First();
        }

        /// <summary>
        /// 延伸两条 MEP 曲线至其交点（直线段适用）。
        /// Extend two MEP curves to their intersection point (for straight segments only).
        /// </summary>
        private static XYZ ExtendMepCurvesToIntersection(Document document, Element first, Element second)
        {
            MEPCurve firstCurve = first as MEPCurve;
            MEPCurve secondCurve = second as MEPCurve;
            if (firstCurve == null || secondCurve == null)
            {
                throw new BridgeCommandException("extend_to_intersection 仅支持管道 / 风管 / 线管 / 桥架等 MEP 曲线。");
            }
            LocationCurve firstLocation = firstCurve.Location as LocationCurve;
            LocationCurve secondLocation = secondCurve.Location as LocationCurve;
            if (firstLocation == null || secondLocation == null)
            {
                throw new BridgeCommandException("extend_to_intersection 目标缺少 LocationCurve。");
            }
            Line firstLine = firstLocation.Curve as Line;
            Line secondLine = secondLocation.Curve as Line;
            if (firstLine == null || secondLine == null)
            {
                throw new BridgeCommandException("extend_to_intersection 目前只支持直线段 MEP 曲线。");
            }

            IntersectionResultArray results;
            SetComparisonResult comparison = firstLine.Intersect(secondLine, out results);
            if (comparison != SetComparisonResult.Overlap || results == null || results.Size == 0)
            {
                throw new BridgeCommandException("两条管线平行或不相交，无法延伸到交点。");
            }
            XYZ intersection = results.get_Item(0).XYZPoint;
            if (intersection == null)
            {
                throw new BridgeCommandException("未能计算出两条管线的交点。");
            }

            firstLocation.Curve = TrimLineToPoint(firstLine, intersection);
            secondLocation.Curve = TrimLineToPoint(secondLine, intersection);
            document.Regenerate();
            return intersection;
        }

        /// <summary>
        /// 将直线修剪到指定点（保留距该点较近的端点到该点之间的线段）。
        /// Trim a line to a given point (keeps the segment from the nearer endpoint to the point).
        /// </summary>
        private static Line TrimLineToPoint(Line line, XYZ point)
        {
            XYZ start = line.GetEndPoint(0);
            XYZ end = line.GetEndPoint(1);
            if (start.DistanceTo(point) < 1e-8 || end.DistanceTo(point) < 1e-8)
            {
                throw new BridgeCommandException("交点与管线端点重合，无需延伸；请去掉 extend_to_intersection。");
            }
            return start.DistanceTo(point) > end.DistanceTo(point)
                ? Line.CreateBound(start, point)
                : Line.CreateBound(point, end);
        }

        /// <summary>
        /// 获取 MEP 元素的端点连接器列表。
        /// Get the list of end-type connectors for an MEP element.
        /// </summary>
        private static List<Connector> GetConnectors(Element element)
        {
            ConnectorManager manager = null;
            MEPCurve curve = element as MEPCurve;
            if (curve != null)
            {
                manager = curve.ConnectorManager;
            }
            else
            {
                FamilyInstance instance = element as FamilyInstance;
                if (instance != null && instance.MEPModel != null)
                {
                    manager = instance.MEPModel.ConnectorManager;
                }
            }
            if (manager == null)
            {
                return new List<Connector>();
            }
            var result = new List<Connector>();
            foreach (Connector connector in manager.Connectors)
            {
                if (connector.ConnectorType == ConnectorType.End)
                {
                    result.Add(connector);
                }
            }
            return result;
        }

        /// <summary>
        /// 解析结构类型字符串到 StructuralType 枚举。
        /// Parse a structural type string to the StructuralType enum.
        /// </summary>
        private static StructuralType ResolveStructuralType(IDictionary<string, object> arguments, StructuralType defaultValue)
        {
            string requested = PlanValues.String(arguments, null, "structural_type");
            if (string.IsNullOrWhiteSpace(requested))
            {
                return defaultValue;
            }
            StructuralType parsed;
            if (!Enum.TryParse(requested, true, out parsed))
            {
                throw new BridgeCommandException("structural_type 无效：" + requested);
            }
            return parsed;
        }

        /// <summary>
        /// 返回延迟放置结果（预览时前置引用尚未就绪）。
        /// Return a deferred placement result (preceding reference not yet available in preview).
        /// </summary>
        private static Dictionary<string, object> DeferredPlacement(
            Dictionary<string, object> data,
            string reference)
        {
            data["deferred"] = true;
            data["reason"] = "preview 中前置元素引用尚无真实 ID：" + reference;
            return data;
        }

        /// <summary>
        /// 解析目标视图（必须是有效非样板视图）。
        /// Resolve a target view (must be a valid non-template view).
        /// </summary>
        private static View ResolveTargetView(
            PlanExecutionContext context,
            IDictionary<string, object> arguments,
            params string[] names)
        {
            ElementId id = context.ResolveSingleElementId(arguments, names);
            if (id.Value == ElementId.InvalidElementId.Value)
            {
                throw new BridgeCommandException("预览中视图引用尚无真实 ID。请将该步骤单独预览，或直接指定已有 view_id。");
            }
            View view = context.Document.GetElement(id) as View;
            if (view == null || view.IsTemplate)
            {
                throw new BridgeCommandException("参数 " + string.Join("/", names) + " 必须指向有效非样板视图。");
            }
            return view;
        }

        /// <summary>
        /// 解析或创建工作平面。可复用已有工作平面或根据法向量创建新的。
        /// Resolve or create a sketch plane. Reuses an existing one or creates from a normal vector.
        /// </summary>
        private static SketchPlane ResolveOrCreateWorkPlane(
            PlanExecutionContext context,
            IDictionary<string, object> arguments,
            XYZ origin)
        {
            object rawId = PlanValues.Get(arguments, "work_plane_id", "sketch_plane_id");
            if (rawId != null)
            {
                SketchPlane existing = context.Document.GetElement(
                    new ElementId(RevitLookups.ParsePositiveId(rawId, "work_plane_id"))) as SketchPlane;
                if (existing == null)
                {
                    throw new BridgeCommandException("work_plane_id 不是有效工作平面。");
                }
                return existing;
            }
            Dictionary<string, object> normalValues = PlanValues.Get(arguments, "work_plane_normal", "normal") == null
                ? null
                : PlanValues.Dictionary(PlanValues.Get(arguments, "work_plane_normal", "normal"), "work_plane_normal");
            XYZ normal = normalValues == null
                ? XYZ.BasisZ
                : new XYZ(
                    PlanValues.Number(normalValues, 0.0, "x"),
                    PlanValues.Number(normalValues, 0.0, "y"),
                    PlanValues.Number(normalValues, 0.0, "z"));
            if (normal.GetLength() < 1e-8)
            {
                throw new BridgeCommandException("work_plane_normal 不能为零向量。");
            }
            return SketchPlane.Create(
                context.Document,
                Plane.CreateByNormalAndOrigin(normal.Normalize(), origin));
        }

        /// <summary>
        /// 读取可选点参数。
        /// Read an optional point parameter.
        /// </summary>
        private static XYZ ReadOptionalPoint(IDictionary<string, object> arguments, params string[] fieldNames)
        {
            foreach (string fieldName in fieldNames)
            {
                if (PlanValues.Get(arguments, fieldName) != null)
                {
                    return PlanValues.Point(arguments, fieldName);
                }
            }
            throw new BridgeCommandException("缺少参数：" + string.Join("/", fieldNames));
        }

        /// <summary>
        /// 读取方向向量参数并归一化。
        /// Read a direction vector parameter and normalize it.
        /// </summary>
        private static XYZ ReadDirectionVector(IDictionary<string, object> arguments, params string[] fieldNames)
        {
            object raw = PlanValues.Get(arguments, fieldNames);
            if (raw == null)
            {
                throw new BridgeCommandException("缺少参数：" + string.Join("/", fieldNames));
            }
            Dictionary<string, object> values = PlanValues.Dictionary(raw, fieldNames[0]);
            XYZ direction = new XYZ(
                PlanValues.Number(values, 0.0, "x"),
                PlanValues.Number(values, 0.0, "y"),
                PlanValues.Number(values, 0.0, "z"));
            if (direction.GetLength() < 1e-9)
            {
                throw new BridgeCommandException(fieldNames[0] + " 不能为零向量。");
            }
            return direction.Normalize();
        }

        /// <summary>
        /// 读取参考方向（放置族时的方向参数）。
        /// Read the reference direction parameter (for family instance placement).
        /// </summary>
        private static XYZ ReadReferenceDirection(IDictionary<string, object> arguments)
        {
            object raw = PlanValues.Get(arguments, "reference_direction", "direction");
            if (raw == null)
            {
                return XYZ.BasisX;
            }
            Dictionary<string, object> values = PlanValues.Dictionary(raw, "reference_direction");
            XYZ direction = new XYZ(
                PlanValues.Number(values, 0.0, "x"),
                PlanValues.Number(values, 0.0, "y"),
                PlanValues.Number(values, 0.0, "z"));
            if (direction.GetLength() < 1e-8)
            {
                throw new BridgeCommandException("reference_direction 不能为零向量。");
            }
            return direction.Normalize();
        }

        /// <summary>
        /// 查找宿主元素上指定索引的面引用。
        /// Find a face reference on a host element by index.
        /// </summary>
        private static Reference FindFaceReference(Element host, int requestedIndex)
        {
            if (requestedIndex < 0)
            {
                throw new BridgeCommandException("host_face_index 必须大于或等于 0。");
            }
            var references = new List<Reference>();
            var options = new Options
            {
                ComputeReferences = true,
                IncludeNonVisibleObjects = false,
                DetailLevel = ViewDetailLevel.Fine
            };
            CollectFaceReferences(host.get_Geometry(options), references);
            if (requestedIndex >= references.Count)
            {
                throw new BridgeCommandException(
                    "host_face_index=" + requestedIndex + " 超出宿主可引用面的数量 " + references.Count + "。");
            }
            return references[requestedIndex];
        }

        /// <summary>
        /// 递归收集几何中的面引用。
        /// Recursively collect face references from geometry.
        /// </summary>
        private static void CollectFaceReferences(GeometryElement geometry, ICollection<Reference> target)
        {
            if (geometry == null)
            {
                return;
            }
            foreach (GeometryObject item in geometry)
            {
                Solid solid = item as Solid;
                if (solid != null && solid.Faces != null)
                {
                    foreach (Face face in solid.Faces)
                    {
                        if (face.Reference != null)
                        {
                            target.Add(face.Reference);
                        }
                    }
                    continue;
                }
                GeometryInstance instance = item as GeometryInstance;
                if (instance != null)
                {
                    CollectFaceReferences(instance.GetInstanceGeometry(), target);
                }
            }
        }

        /// <summary>
        /// 读取自适应点列表。
        /// Read a list of adaptive placement points.
        /// </summary>
        private static List<XYZ> ReadAdaptivePoints(IDictionary<string, object> arguments)
        {
            List<Dictionary<string, object>> raw = PlanValues.DictionaryList(
                PlanValues.Get(arguments, "adaptive_points", "points"),
                "adaptive_points");
            if (raw.Count == 0)
            {
                throw new BridgeCommandException("自适应族需要 adaptive_points 数组。");
            }
            var points = new List<XYZ>();
            foreach (Dictionary<string, object> item in raw)
            {
                double x = PlanValues.RequireMillimeters(item, "x", "x_mm");
                double y = PlanValues.RequireMillimeters(item, "y", "y_mm");
                double z = PlanValues.Millimeters(item, 0.0, "z", "z_mm");
                points.Add(new XYZ(PlanValues.ToFeet(x), PlanValues.ToFeet(y), PlanValues.ToFeet(z)));
            }
            return points;
        }

        /// <summary>
        /// 激活族类型（如果尚未激活）。
        /// Activate a family symbol if it is not already active.
        /// </summary>
        private static void ActivateSymbol(Document document, FamilySymbol symbol)
        {
            if (!symbol.IsActive)
            {
                symbol.Activate();
                document.Regenerate();
            }
        }

        /// <summary>
        /// 解析或创建墙类型。如指定厚度的类型已存在则复用，否则复制源类型并调整厚度。
        /// Resolve or create a wall type. Reuses an existing type at the target thickness or duplicates and adjusts.
        /// </summary>
        private static WallType ResolveOrCreateWallType(Document document, WallType sourceType, string targetName, double thicknessMm)
        {
            WallType existing = new FilteredElementCollector(document)
                .OfClass(typeof(WallType))
                .Cast<WallType>()
                .FirstOrDefault(type => type.Kind == WallKind.Basic &&
                    string.Equals(type.Name, targetName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (Math.Abs(PlanValues.ToMillimeters(existing.Width) - thicknessMm) > 0.5)
                {
                    throw new BridgeCommandException("墙类型\u201c" + targetName + "\u201d已存在，且厚度不匹配；不会改动既有类型。");
                }
                return existing;
            }

            WallType duplicate = sourceType.Duplicate(targetName) as WallType;
            if (duplicate == null)
            {
                throw new BridgeCommandException("无法复制墙类型\u201c" + sourceType.Name + "\u201d。");
            }
            CompoundStructure structure = duplicate.GetCompoundStructure();
            if (structure == null || structure.LayerCount < 1)
            {
                throw new BridgeCommandException("墙类型\u201c" + sourceType.Name + "\u201d没有可调整的复合结构。");
            }
            double targetTotal = PlanValues.ToFeet(thicknessMm);
            double currentTotal = structure.GetLayers().Sum(layer => layer.Width);
            int index = structure.VariableLayerIndex >= 0 ? structure.VariableLayerIndex : 0;
            double adjustedWidth = structure.GetLayers()[index].Width + targetTotal - currentTotal;
            if (adjustedWidth <= 0.0)
            {
                throw new BridgeCommandException("指定墙厚会使墙类型\u201c" + sourceType.Name + "\u201d的层厚无效。");
            }
            structure.SetLayerWidth(index, adjustedWidth);
            duplicate.SetCompoundStructure(structure);
            return duplicate;
        }
    }
}
