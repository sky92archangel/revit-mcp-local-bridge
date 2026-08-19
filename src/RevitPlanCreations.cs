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
    internal static class RevitPlanCreations
    {
        public static Dictionary<string, object> CreateLevel(PlanStep step, PlanExecutionContext context)
        {
            double elevationMm = PlanValues.RequireMillimeters(step.Arguments, "elevation_mm", "elevation");
            string name = PlanValues.String(step.Arguments, null, "name");
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
            if (context.Preview)
            {
                return data;
            }

            Level level = Level.Create(context.Document, PlanValues.ToFeet(elevationMm));
            if (!string.IsNullOrWhiteSpace(name))
            {
                level.Name = name;
            }
            data["element_id"] = level.Id.IntegerValue;
            data["element_ids"] = new[] { level.Id.IntegerValue };
            data["name"] = level.Name;
            return data;
        }

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
            data["element_id"] = grid.Id.IntegerValue;
            data["element_ids"] = new[] { grid.Id.IntegerValue };
            data["name"] = grid.Name;
            return data;
        }

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
            data["element_id"] = wall.Id.IntegerValue;
            data["element_ids"] = new[] { wall.Id.IntegerValue };
            data["type_target"] = targetType.Name;
            return data;
        }

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
                { "type_id", floorType.Id.IntegerValue },
                { "structural", structural },
                { "offset_mm", offsetMm },
                { "boundary_segment_count", profile.Size }
            };
            if (context.Preview)
            {
                return data;
            }

            Floor floor = context.Document.Create.NewFloor(profile, floorType, level, structural);
            data["element_id"] = floor.Id.IntegerValue;
            data["element_ids"] = new[] { floor.Id.IntegerValue };
            return data;
        }

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
            data["element_id"] = room.Id.IntegerValue;
            data["element_ids"] = new[] { room.Id.IntegerValue };
            data["name"] = room.Name;
            data["number"] = room.Number;
            return data;
        }

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
            data["element_id"] = space.Id.IntegerValue;
            data["element_ids"] = new[] { space.Id.IntegerValue };
            data["name"] = space.Name;
            data["number"] = space.Number;
            return data;
        }

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

            XYZ direction = (end - start).Normalize();
            XYZ seed = Math.Abs(direction.DotProduct(XYZ.BasisZ)) < 0.9 ? XYZ.BasisZ : XYZ.BasisX;
            XYZ normal = direction.CrossProduct(seed).Normalize();
            SketchPlane sketchPlane = SketchPlane.Create(
                context.Document,
                Plane.CreateByNormalAndOrigin(normal, start));
            ModelCurve curve = context.Document.Create.NewModelCurve(
                Line.CreateBound(start, end),
                sketchPlane);
            data["element_id"] = curve.Id.IntegerValue;
            data["element_ids"] = new[] { curve.Id.IntegerValue };
            if (!string.IsNullOrWhiteSpace(name))
            {
                data["name_applied"] = false;
            }
            return data;
        }

        public static Dictionary<string, object> CreateView(PlanStep step, PlanExecutionContext context)
        {
            string kind = PlanValues.String(step.Arguments, "3d", "kind", "view_kind", "view_type")
                .Trim()
                .ToLowerInvariant()
                .Replace("-", "_")
                .Replace(" ", "_");
            ViewFamily family;
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
            var data = new Dictionary<string, object>
            {
                { "kind", kind },
                { "view_family", family.ToString() },
                { "type", type.Name },
                { "type_id", type.Id.IntegerValue },
                { "level", level == null ? null : level.Name },
                { "perspective", perspective },
                { "name", name }
            };
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
            data["element_id"] = view.Id.IntegerValue;
            data["element_ids"] = new[] { view.Id.IntegerValue };
            data["name"] = view.Name;
            return data;
        }

        public static Dictionary<string, object> CreateSheet(PlanStep step, PlanExecutionContext context)
        {
            FamilySymbol titleBlock = ResolveOptionalTitleBlock(context.Document, step.Arguments);
            string sheetNumber = PlanValues.String(step.Arguments, null, "sheet_number", "number");
            string name = PlanValues.String(step.Arguments, null, "name", "sheet_name");
            var data = new Dictionary<string, object>
            {
                { "title_block_type_id", titleBlock == null ? (object)null : titleBlock.Id.IntegerValue },
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
            data["element_id"] = sheet.Id.IntegerValue;
            data["element_ids"] = new[] { sheet.Id.IntegerValue };
            data["sheet_number"] = sheet.SheetNumber;
            data["name"] = sheet.Name;
            return data;
        }

        public static Dictionary<string, object> PlaceViewOnSheet(PlanStep step, PlanExecutionContext context)
        {
            ElementId sheetId = context.ResolveSingleElementId(step.Arguments, "sheet_id", "sheet", "target_sheet");
            ElementId viewId = context.ResolveSingleElementId(step.Arguments, "view_id", "view", "target_view");
            if (sheetId.IntegerValue == ElementId.InvalidElementId.IntegerValue ||
                viewId.IntegerValue == ElementId.InvalidElementId.IntegerValue)
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
                throw new BridgeCommandException("sheet_id 不是有效图纸：" + sheetId.IntegerValue);
            }
            if (view == null || view.IsTemplate)
            {
                throw new BridgeCommandException("view_id 不是可放置的视图：" + viewId.IntegerValue);
            }
            XYZ point = PlanValues.Point(step.Arguments, "point");
            var data = new Dictionary<string, object>
            {
                { "sheet_id", sheetId.IntegerValue },
                { "view_id", viewId.IntegerValue },
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
            data["element_id"] = viewport.Id.IntegerValue;
            data["element_ids"] = new[] { viewport.Id.IntegerValue };
            return data;
        }

        public static Dictionary<string, object> CreateOpening(PlanStep step, PlanExecutionContext context)
        {
            ElementId hostId = context.ResolveSingleElementId(step.Arguments, "host_id", "host", "wall_id", "wall");
            if (hostId.IntegerValue == ElementId.InvalidElementId.IntegerValue)
            {
                return new Dictionary<string, object>
                {
                    { "deferred", true },
                    { "reason", "preview 中前置墙体引用尚无真实 ID。" }
                };
            }
            Wall wall = context.Document.GetElement(hostId) as Wall;
            if (wall == null)
            {
                throw new BridgeCommandException("create_opening.host_id 必须指向墙体。");
            }
            XYZ start = PlanValues.Point(step.Arguments, "start");
            XYZ end = PlanValues.Point(step.Arguments, "end");
            if (start.DistanceTo(end) < 1e-8)
            {
                throw new BridgeCommandException("洞口 start 与 end 不能重合。");
            }
            var data = new Dictionary<string, object>
            {
                { "host_id", hostId.IntegerValue },
                { "start", PlanValues.PointData(start) },
                { "end", PlanValues.PointData(end) }
            };
            if (context.Preview)
            {
                return data;
            }

            Opening opening = context.Document.Create.NewOpening(wall, start, end);
            data["element_id"] = opening.Id.IntegerValue;
            data["element_ids"] = new[] { opening.Id.IntegerValue };
            return data;
        }

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
                symbol.Category.Id.IntegerValue != new ElementId(BuiltInCategory.OST_TitleBlocks).IntegerValue)
            {
                throw new BridgeCommandException("指定的 title_block 不是图框族类型。");
            }
            return symbol;
        }

        public static Dictionary<string, object> CreateDirectShape(PlanStep step, PlanExecutionContext context)
        {
            ElementId categoryId = RevitLookups.ResolveCategoryId(
                context.Document, step.Arguments, BuiltInCategory.OST_GenericModel);
            if (!DirectShape.IsValidCategoryId(categoryId, context.Document))
            {
                throw new BridgeCommandException("DirectShape 不支持类别 ID：" + categoryId.IntegerValue);
            }

            string name = PlanValues.String(step.Arguments, step.Id, "name");
            string materialName = PlanValues.String(step.Arguments, null, "material", "material_name");
            bool createMaterial = PlanValues.Boolean(step.Arguments, false, "create_material_if_missing");
            Material material = RevitLookups.FindMaterial(context.Document, materialName);
            if (!string.IsNullOrWhiteSpace(materialName) && material == null && !createMaterial)
            {
                throw new BridgeCommandException("找不到材质“" + materialName + "”。设置 create_material_if_missing=true 可在执行时创建。");
            }
            Dictionary<string, object> geometryData = RevitGeometryFactory.DescribeGeometry(step.Arguments);
            var data = new Dictionary<string, object>
            {
                { "name", name },
                { "category_id", categoryId.IntegerValue },
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
            data["element_id"] = shape.Id.IntegerValue;
            data["element_ids"] = new[] { shape.Id.IntegerValue };
            return data;
        }

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
            Level level = RevitLookups.ResolveLevel(context.Document, step.Arguments);
            string normalizedKind = kind.Trim().ToLowerInvariant();
            var data = new Dictionary<string, object>
            {
                { "kind", normalizedKind },
                { "level", level.Name },
                { "start", PlanValues.PointData(start) },
                { "end", PlanValues.PointData(end) }
            };

            Element created;
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

            data["element_id"] = created.Id.IntegerValue;
            data["element_ids"] = new[] { created.Id.IntegerValue };
            return data;
        }

        public static Dictionary<string, object> ConnectMep(PlanStep step, PlanExecutionContext context)
        {
            ElementId aId = context.ResolveSingleElementId(step.Arguments, "element_a", "from", "first");
            ElementId bId = context.ResolveSingleElementId(step.Arguments, "element_b", "to", "second");
            string fitting = PlanValues.String(step.Arguments, "auto", "fitting").ToLowerInvariant();
            if (aId.IntegerValue == ElementId.InvalidElementId.IntegerValue ||
                bId.IntegerValue == ElementId.InvalidElementId.IntegerValue)
            {
                return new Dictionary<string, object> { { "deferred", true }, { "reason", "preview 中前置元素引用尚无真实 ID。" } };
            }
            Element first = RequireElement(context.Document, aId, "element_a");
            Element second = RequireElement(context.Document, bId, "element_b");
            Connector firstConnector = FindConnector(first, step.Arguments, "connector_a_index", second);
            Connector secondConnector = FindConnector(second, step.Arguments, "connector_b_index", first);
            var data = new Dictionary<string, object>
            {
                { "element_a", aId.IntegerValue },
                { "element_b", bId.IntegerValue },
                { "fitting", fitting },
                { "connector_a_origin", PlanValues.PointData(firstConnector.Origin) },
                { "connector_b_origin", PlanValues.PointData(secondConnector.Origin) }
            };
            if (context.Preview)
            {
                return data;
            }

            Element fittingElement = null;
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
                    data["element_c"] = cId.IntegerValue;
                    data["connector_c_origin"] = PlanValues.PointData(thirdConnector.Origin);
                    break;
                default:
                    throw new BridgeCommandException("connect_mep.fitting 仅支持 auto、direct、elbow、union、tee。");
            }
            if (fittingElement != null)
            {
                data["element_id"] = fittingElement.Id.IntegerValue;
                data["element_ids"] = new[] { fittingElement.Id.IntegerValue };
            }
            return data;
        }

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
                { "type_id", symbol.Id.IntegerValue },
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
                    if (hostId.IntegerValue == ElementId.InvalidElementId.IntegerValue)
                    {
                        return DeferredPlacement(data, "$host");
                    }
                    Element host = RequireElement(context.Document, hostId, "host_id");
                    data["point"] = PlanValues.PointData(hostedPoint);
                    data["host_id"] = hostId.IntegerValue;
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
                        if (faceHostId.IntegerValue == ElementId.InvalidElementId.IntegerValue)
                        {
                            return DeferredPlacement(data, "$host");
                        }
                        Element faceHost = RequireElement(context.Document, faceHostId, "host_id");
                        int faceIndex = PlanValues.Integer(step.Arguments, 0, "host_face_index", "face_index");
                        Reference face = FindFaceReference(faceHost, faceIndex);
                        XYZ direction = ReadReferenceDirection(step.Arguments);
                        data["host_id"] = faceHostId.IntegerValue;
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
                        data["work_plane_id"] = workPlane.Id.IntegerValue;
                        ActivateSymbol(context.Document, symbol);
                        instance = context.Document.Create.NewFamilyInstance(workPlanePoint, symbol, workPlane, structuralType);
                    }
                    break;

                case FamilyPlacementType.ViewBased:
                    XYZ viewPoint = PlanValues.Point(step.Arguments, "point");
                    View view = ResolveTargetView(context, step.Arguments, "view_id", "view");
                    data["point"] = PlanValues.PointData(viewPoint);
                    data["view_id"] = view.Id.IntegerValue;
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
                        data["view_id"] = curveView.Id.IntegerValue;
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
                            throw new BridgeCommandException("未能定位自适应族放置点：" + pointIds[index].IntegerValue);
                        }
                        referencePoint.Position = adaptivePoints[index];
                    }
                    data["adaptive_point_ids"] = pointIds.Select(id => id.IntegerValue).ToArray();
                    break;

                default:
                    throw new BridgeCommandException(
                        "族“" + symbol.Family.Name + "”使用暂未识别的放置类型：" + placementType + "。");
            }
            data["element_id"] = instance.Id.IntegerValue;
            data["element_ids"] = new[] { instance.Id.IntegerValue };
            return data;
        }

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
                { "type_id", symbol.Id.IntegerValue },
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
                data["element_id"] = lineInstance.Id.IntegerValue;
                data["element_ids"] = new[] { lineInstance.Id.IntegerValue };
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
                data["element_id"] = column.Id.IntegerValue;
                data["element_ids"] = new[] { column.Id.IntegerValue };
                return data;
            }
            throw new BridgeCommandException("create_structural_member.kind 仅支持 beam、brace、column。");
        }

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

        private static Element RequireElement(Document document, ElementId id, string fieldName)
        {
            Element element = document.GetElement(id);
            if (element == null)
            {
                throw new BridgeCommandException("找不到 " + fieldName + "=" + id.IntegerValue + " 对应元素。");
            }
            return element;
        }

        private static Connector FindConnector(
            Element element,
            IDictionary<string, object> arguments,
            string indexName,
            Element otherElement)
        {
            List<Connector> connectors = GetConnectors(element);
            if (connectors.Count == 0)
            {
                throw new BridgeCommandException("元素 " + element.Id.IntegerValue + " 没有可连接的 MEP Connector。");
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

            List<Connector> otherConnectors = GetConnectors(otherElement);
            if (otherConnectors.Count == 0)
            {
                return connectors[0];
            }
            return connectors.OrderBy(connector => otherConnectors.Min(other => connector.Origin.DistanceTo(other.Origin))).First();
        }

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

        private static Dictionary<string, object> DeferredPlacement(
            Dictionary<string, object> data,
            string reference)
        {
            data["deferred"] = true;
            data["reason"] = "preview 中前置元素引用尚无真实 ID：" + reference;
            return data;
        }

        private static View ResolveTargetView(
            PlanExecutionContext context,
            IDictionary<string, object> arguments,
            params string[] names)
        {
            ElementId id = context.ResolveSingleElementId(arguments, names);
            if (id.IntegerValue == ElementId.InvalidElementId.IntegerValue)
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

        private static void ActivateSymbol(Document document, FamilySymbol symbol)
        {
            if (!symbol.IsActive)
            {
                symbol.Activate();
                document.Regenerate();
            }
        }

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
                    throw new BridgeCommandException("墙类型“" + targetName + "”已存在，且厚度不匹配；不会改动既有类型。");
                }
                return existing;
            }

            WallType duplicate = sourceType.Duplicate(targetName) as WallType;
            if (duplicate == null)
            {
                throw new BridgeCommandException("无法复制墙类型“" + sourceType.Name + "”。");
            }
            CompoundStructure structure = duplicate.GetCompoundStructure();
            if (structure == null || structure.LayerCount < 1)
            {
                throw new BridgeCommandException("墙类型“" + sourceType.Name + "”没有可调整的复合结构。");
            }
            double targetTotal = PlanValues.ToFeet(thicknessMm);
            double currentTotal = structure.GetLayers().Sum(layer => layer.Width);
            int index = structure.VariableLayerIndex >= 0 ? structure.VariableLayerIndex : 0;
            double adjustedWidth = structure.GetLayers()[index].Width + targetTotal - currentTotal;
            if (adjustedWidth <= 0.0)
            {
                throw new BridgeCommandException("指定墙厚会使墙类型“" + sourceType.Name + "”的层厚无效。");
            }
            structure.SetLayerWidth(index, adjustedWidth);
            duplicate.SetCompoundStructure(structure);
            return duplicate;
        }
    }
}
