using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitCommandBridge
{
    /// <summary>
    /// 兼容操作模式的命令执行器：处理 health、list_levels、create_wall 等单步命令。 / Legacy command executor handling single-step operations such as health, list_levels, create_wall, etc.
    /// </summary>
    internal static class RevitCommandExecutor
    {
        private const double FeetPerMillimeter = 1.0 / 304.8;

        /// <summary>
        /// 按 operation 分派到对应的处理函数。 / Dispatches to the appropriate handler based on the operation name.
        /// </summary>
        public static BridgeResponse Execute(UIApplication uiApplication, BridgeRequest request)
        {
            if (uiApplication == null)
            {
                throw new BridgeCommandException("Revit UI 应用不可用。");
            }

            string operation = NormalizeOperation(request.Operation);
            if (operation == "health")
            {
                return Health(uiApplication, request.DocumentTitle);
            }

            if (operation == "list_family_templates")
            {
                return RevitFamilyOperations.ListFamilyTemplates(uiApplication, request);
            }

            if (operation == "new_project")
            {
                return CreateProject(uiApplication, request);
            }

            if (uiApplication.ActiveUIDocument == null)
            {
                throw new BridgeCommandException("请先在 Revit 中打开一个项目文档。");
            }

            Document document = uiApplication.ActiveUIDocument.Document;
            if (document == null || !document.IsValidObject)
            {
                throw new BridgeCommandException("当前 Revit 文档不可用。");
            }

            if (!string.IsNullOrWhiteSpace(request.DocumentTitle) &&
                !string.Equals(request.DocumentTitle, document.Title, StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeCommandException(
                    "当前文档为“" + document.Title + "”，与命令目标“" + request.DocumentTitle + "”不一致。");
            }

            if (document.IsReadOnly && IsWriteOperation(operation, request))
            {
                throw new BridgeCommandException("当前 Revit 文档为只读，不能建模。");
            }

            switch (operation)
            {
                case "list_levels":
                    return ListLevels(document);
                case "list_wall_types":
                    return ListWallTypes(document);
                case "new_project":
                    return CreateProject(uiApplication, request);
                case "execute_plan":
                    return PlanCommandExecutor.Execute(uiApplication, document, request);
                case "create_level":
                    return CreateLevel(document, request);
                case "create_grid":
                    return CreateGrid(document, request);
                case "create_rectangle_walls":
                    return CreateRectangleWalls(document, request);
                case "create_wall":
                    return CreateWall(document, request);
                case "create_family":
                    return RevitFamilyOperations.CreateFamily(uiApplication, document, request);
                case "load_family":
                    return RevitFamilyOperations.LoadFamily(uiApplication, document, request);
                default:
                    throw new BridgeCommandException(
                        "不支持 operation=“" + request.Operation + "”。支持：health、list_family_templates、create_family、load_family、execute_plan，以及兼容操作 list_levels、list_wall_types、create_level、create_grid、create_rectangle_walls、create_wall、new_project。");
            }
        }

        /// <summary>
        /// 健康检查：返回桥接状态、Revit 版本、文档信息等。 / Health check: returns bridge status, Revit version, document info, etc.
        /// </summary>
        private static BridgeResponse Health(UIApplication uiApplication, string expectedDocumentTitle)
        {
            var data = new Dictionary<string, object>
            {
                { "bridge_running", BridgeRuntime.IsRunning },
                { "revit_api", BridgeBuildInfo.RevitVersion },
                { "protocol", BridgeProtocol.Version },
                { "supported_operations", new[]
                    {
                        "health", "execute_plan", "list_levels", "list_wall_types", "new_project", "create_level", "create_grid", "create_rectangle_walls", "create_wall"
                        , "list_family_templates", "create_family", "load_family"
                    }
                }
            };

            if (uiApplication.ActiveUIDocument == null)
            {
                data["document_open"] = false;
                if (!string.IsNullOrWhiteSpace(expectedDocumentTitle))
                {
                    return BridgeResponse.Failure("当前没有打开项目文档，无法匹配 document_title。", data);
                }

                return BridgeResponse.Success("completed", "命令桥运行中，但尚未打开项目文档。", data);
            }

            Document document = uiApplication.ActiveUIDocument.Document;
            if (document == null || !document.IsValidObject)
            {
                return BridgeResponse.Failure("当前 Revit 文档不可用。", data);
            }

            data["document_open"] = true;
            data["document_title"] = document.Title;
            data["document_path"] = document.PathName ?? string.Empty;
            data["read_only"] = document.IsReadOnly;
            if (!string.IsNullOrWhiteSpace(expectedDocumentTitle) &&
                !string.Equals(expectedDocumentTitle, document.Title, StringComparison.OrdinalIgnoreCase))
            {
                return BridgeResponse.Failure(
                    "当前文档为“" + document.Title + "”，与命令目标“" + expectedDocumentTitle + "”不一致。",
                    data);
            }

            return BridgeResponse.Success("completed", "桥接已连接到当前 Revit 文档。", data);
        }

        /// <summary>
        /// 创建并可选地保存新 Revit 项目。 / Creates and optionally saves a new Revit project.
        /// </summary>
        private static BridgeResponse CreateProject(UIApplication uiApplication, BridgeRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.DocumentTitle))
            {
                throw new BridgeCommandException("new_project 不接受 document_title。请在创建后使用 health 查询新文档标题。");
            }

            string templatePath = BridgeArguments.GetString(request, null, "template_path", "template");
            string savePath = BridgeArguments.GetString(request, null, "save_path", "path");
            bool overwriteFile = BridgeArguments.GetBoolean(request, false, "overwrite_file", "overwrite");
            var plan = new Dictionary<string, object>
            {
                { "operation", "new_project" },
                { "template_path", templatePath },
                { "unit_system", string.IsNullOrWhiteSpace(templatePath) ? "Metric" : null },
                { "save_path", savePath },
                { "overwrite_file", overwriteFile }
            };
            if (request.Preview)
            {
                return BridgeResponse.Success("preview", "预览：将创建一个未保存的新项目。", plan);
            }

            Document created;
            if (string.IsNullOrWhiteSpace(templatePath))
            {
                created = uiApplication.Application.NewProjectDocument(UnitSystem.Metric);
            }
            else
            {
                string normalizedTemplatePath = templatePath.Trim();
                if (!File.Exists(normalizedTemplatePath))
                {
                    throw new BridgeCommandException("项目样板不存在：" + normalizedTemplatePath);
                }

                created = uiApplication.Application.NewProjectDocument(normalizedTemplatePath);
                plan["template_path"] = normalizedTemplatePath;
            }

            if (created == null || !created.IsValidObject)
            {
                throw new BridgeCommandException("Revit 未能创建新项目。");
            }

            if (!string.IsNullOrWhiteSpace(savePath))
            {
                string normalizedSavePath = Path.GetFullPath(savePath.Trim());
                if (!string.Equals(Path.GetExtension(normalizedSavePath), ".rvt", StringComparison.OrdinalIgnoreCase))
                {
                    throw new BridgeCommandException("new_project.save_path 必须以 .rvt 结尾。");
                }
                if (File.Exists(normalizedSavePath) && !overwriteFile)
                {
                    throw new BridgeCommandException("目标项目文件已存在。设置 overwrite_file=true 才会覆盖：" + normalizedSavePath);
                }
                string directory = Path.GetDirectoryName(normalizedSavePath);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    throw new BridgeCommandException("new_project.save_path 必须包含有效目录。");
                }
                Directory.CreateDirectory(directory);
                created.SaveAs(normalizedSavePath, new SaveAsOptions { OverwriteExistingFile = overwriteFile });
                UIDocument activated = uiApplication.OpenAndActivateDocument(normalizedSavePath);
                plan["save_path"] = normalizedSavePath;
                plan["document_title"] = activated == null || activated.Document == null ? created.Title : activated.Document.Title;
                plan["active"] = activated != null && activated.Document != null;
                return BridgeResponse.Success("completed", "已创建并打开项目“" + plan["document_title"] + "”。", plan);
            }

            UIDocument active = uiApplication.ActiveUIDocument;
            plan["document_title"] = created.Title;
            plan["active"] = active != null && active.Document != null && active.Document.Equals(created);
            plan["requires_ui_activation"] = !((bool)plan["active"]);
            return BridgeResponse.Success("completed", "已创建未保存的新项目“" + created.Title + "”。", plan);
        }

        /// <summary>
        /// 列出项目中所有标高及其高程（mm）。 / Lists all levels in the project with their elevations (mm).
        /// </summary>
        private static BridgeResponse ListLevels(Document document)
        {
            List<Level> levels = new FilteredElementCollector(document)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(candidateLevel => candidateLevel.Elevation)
                .ToList();

            var items = new List<Dictionary<string, object>>();
            foreach (Level level in levels)
            {
                items.Add(new Dictionary<string, object>
                {
                    { "id", (int)level.Id.Value },
                    { "name", level.Name },
                    { "elevation_mm", RoundMillimeters(level.Elevation) }
                });
            }

            return BridgeResponse.Success(
                "completed",
                "读取到 " + items.Count + " 个标高。",
                new Dictionary<string, object> { { "levels", items } });
        }

        /// <summary>
        /// 列出项目中所有基本墙类型。 / Lists all basic wall types in the project.
        /// </summary>
        private static BridgeResponse ListWallTypes(Document document)
        {
            List<WallType> wallTypes = BasicWallTypes(document).OrderBy(type => type.Name).ToList();
            var items = new List<Dictionary<string, object>>();
            foreach (WallType wallType in wallTypes)
            {
                items.Add(new Dictionary<string, object>
                {
                    { "id", (int)wallType.Id.Value },
                    { "name", wallType.Name },
                    { "width_mm", RoundMillimeters(wallType.Width) }
                });
            }

            return BridgeResponse.Success(
                "completed",
                "读取到 " + items.Count + " 个基本墙类型。",
                new Dictionary<string, object> { { "wall_types", items } });
        }

        /// <summary>
        /// 创建新标高。 / Creates a new level.
        /// </summary>
        private static BridgeResponse CreateLevel(Document document, BridgeRequest request)
        {
            double elevationMm = BridgeArguments.RequireMillimeters(request, "elevation_mm", "elevation", "标高");
            string name = BridgeArguments.GetString(request, null, "name", "名称");
            var plan = new Dictionary<string, object>
            {
                { "operation", "create_level" },
                { "name", name },
                { "elevation_mm", elevationMm }
            };
            if (request.Preview)
            {
                return BridgeResponse.Success("preview", "预览：将创建标高。", plan);
            }

            Level created;
            using (Transaction transaction = new Transaction(document, "RCB 创建标高"))
            {
                transaction.Start();
                created = Level.Create(document, ToFeet(elevationMm));
                if (!string.IsNullOrWhiteSpace(name))
                {
                    created.Name = name.Trim();
                }

                transaction.Commit();
            }

            plan["id"] = (int)created.Id.Value;
            plan["name"] = created.Name;
            return BridgeResponse.Success("completed", "已创建标高“" + created.Name + "”。", plan);
        }

        /// <summary>
        /// 创建新轴网。 / Creates a new grid line.
        /// </summary>
        private static BridgeResponse CreateGrid(Document document, BridgeRequest request)
        {
            double x1Mm = BridgeArguments.RequireMillimeters(request, "x1_mm", "x1");
            double y1Mm = BridgeArguments.RequireMillimeters(request, "y1_mm", "y1");
            double x2Mm = BridgeArguments.RequireMillimeters(request, "x2_mm", "x2");
            double y2Mm = BridgeArguments.RequireMillimeters(request, "y2_mm", "y2");
            string name = BridgeArguments.GetString(request, null, "name", "名称");

            EnsureDistinctPoints(x1Mm, y1Mm, x2Mm, y2Mm, "轴网起点与终点不能重合。");
            var plan = new Dictionary<string, object>
            {
                { "operation", "create_grid" },
                { "name", name },
                { "start_mm", PointData(x1Mm, y1Mm) },
                { "end_mm", PointData(x2Mm, y2Mm) }
            };
            if (request.Preview)
            {
                return BridgeResponse.Success("preview", "预览：将创建轴网。", plan);
            }

            Grid created;
            using (Transaction transaction = new Transaction(document, "RCB 创建轴网"))
            {
                transaction.Start();
                created = Grid.Create(document, Line.CreateBound(ToXyz(x1Mm, y1Mm), ToXyz(x2Mm, y2Mm)));
                if (!string.IsNullOrWhiteSpace(name))
                {
                    created.Name = name.Trim();
                }

                transaction.Commit();
            }

            plan["id"] = (int)created.Id.Value;
            plan["name"] = created.Name;
            return BridgeResponse.Success("completed", "已创建轴网“" + created.Name + "”。", plan);
        }

        /// <summary>
        /// 创建 4 面闭合的矩形墙。 / Creates 4 closed walls forming a rectangle.
        /// </summary>
        private static BridgeResponse CreateRectangleWalls(Document document, BridgeRequest request)
        {
            double widthMm = BridgeArguments.RequireMillimeters(request, "width_mm", "width", "宽");
            double depthMm = BridgeArguments.RequireMillimeters(request, "depth_mm", "depth", "长", "进深");
            double heightMm = BridgeArguments.GetMillimeters(request, 3000.0, "height_mm", "height", "高度");
            double thicknessMm = BridgeArguments.GetMillimeters(request, 200.0, "thickness_mm", "thickness", "墙厚");
            double xMm = BridgeArguments.GetMillimeters(request, 0.0, "x_mm", "x");
            double yMm = BridgeArguments.GetMillimeters(request, 0.0, "y_mm", "y");
            string levelName = BridgeArguments.GetString(request, null, "level", "level_name", "标高");
            string wallTypeName = BridgeArguments.GetString(request, null, "wall_type", "wall_type_name", "墙类型");
            string requestedTypeName = BridgeArguments.GetString(request, null, "new_wall_type", "new_wall_type_name", "新墙类型");

            if (widthMm <= 0 || depthMm <= 0 || heightMm <= 0 || thicknessMm <= 0)
            {
                throw new BridgeCommandException("矩形墙的宽、长、高、墙厚都必须大于 0。\n");
            }

            Level level = ResolveLevel(document, levelName);
            WallType sourceType = ResolveWallType(document, wallTypeName);
            string effectiveTypeName = string.IsNullOrWhiteSpace(requestedTypeName)
                ? EnsureWallTypeName(sourceType, thicknessMm)
                : requestedTypeName.Trim();

            var plan = new Dictionary<string, object>
            {
                { "operation", "create_rectangle_walls" },
                { "level", level.Name },
                { "wall_type_source", sourceType.Name },
                { "wall_type_target", effectiveTypeName },
                { "origin_mm", PointData(xMm, yMm) },
                { "width_mm", widthMm },
                { "depth_mm", depthMm },
                { "height_mm", heightMm },
                { "thickness_mm", thicknessMm },
                { "wall_count", 4 }
            };
            if (request.Preview)
            {
                return BridgeResponse.Success("preview", "预览：将在“" + level.Name + "”创建 4 面矩形墙。", plan);
            }

            WallType targetType;
            List<Wall> walls = new List<Wall>();
            using (TransactionGroup group = new TransactionGroup(document, "RCB 创建矩形墙"))
            {
                group.Start();
                using (Transaction transaction = new Transaction(document, "RCB 创建墙体"))
                {
                    transaction.Start();
                    targetType = ResolveOrCreateWallType(document, sourceType, effectiveTypeName, thicknessMm);
                    XYZ p1 = ToXyzAtElevation(xMm, yMm, level.Elevation);
                    XYZ p2 = ToXyzAtElevation(xMm + widthMm, yMm, level.Elevation);
                    XYZ p3 = ToXyzAtElevation(xMm + widthMm, yMm + depthMm, level.Elevation);
                    XYZ p4 = ToXyzAtElevation(xMm, yMm + depthMm, level.Elevation);
                    walls.Add(CreateWall(document, p1, p2, targetType, level, heightMm));
                    walls.Add(CreateWall(document, p2, p3, targetType, level, heightMm));
                    walls.Add(CreateWall(document, p3, p4, targetType, level, heightMm));
                    walls.Add(CreateWall(document, p4, p1, targetType, level, heightMm));
                    transaction.Commit();
                }

                group.Assimilate();
            }

            plan["wall_type_target"] = targetType.Name;
            plan["wall_ids"] = walls.Select(wall => (int)wall.Id.Value).ToArray();
            return BridgeResponse.Success("completed", "已创建 4 面矩形墙。", plan);
        }

        /// <summary>
        /// 创建单面墙。 / Creates a single wall.
        /// </summary>
        private static BridgeResponse CreateWall(Document document, BridgeRequest request)
        {
            double x1Mm = BridgeArguments.RequireMillimeters(request, "x1_mm", "x1");
            double y1Mm = BridgeArguments.RequireMillimeters(request, "y1_mm", "y1");
            double x2Mm = BridgeArguments.RequireMillimeters(request, "x2_mm", "x2");
            double y2Mm = BridgeArguments.RequireMillimeters(request, "y2_mm", "y2");
            double heightMm = BridgeArguments.GetMillimeters(request, 3000.0, "height_mm", "height", "高度");
            double thicknessMm = BridgeArguments.GetMillimeters(request, 200.0, "thickness_mm", "thickness", "墙厚");
            string levelName = BridgeArguments.GetString(request, null, "level", "level_name", "标高");
            string wallTypeName = BridgeArguments.GetString(request, null, "wall_type", "wall_type_name", "墙类型");
            string requestedTypeName = BridgeArguments.GetString(request, null, "new_wall_type", "new_wall_type_name", "新墙类型");

            EnsureDistinctPoints(x1Mm, y1Mm, x2Mm, y2Mm, "墙体起点与终点不能重合。");
            if (heightMm <= 0 || thicknessMm <= 0)
            {
                throw new BridgeCommandException("墙高和墙厚必须大于 0。");
            }

            Level level = ResolveLevel(document, levelName);
            WallType sourceType = ResolveWallType(document, wallTypeName);
            string effectiveTypeName = string.IsNullOrWhiteSpace(requestedTypeName)
                ? EnsureWallTypeName(sourceType, thicknessMm)
                : requestedTypeName.Trim();

            var plan = new Dictionary<string, object>
            {
                { "operation", "create_wall" },
                { "level", level.Name },
                { "wall_type_source", sourceType.Name },
                { "wall_type_target", effectiveTypeName },
                { "start_mm", PointData(x1Mm, y1Mm) },
                { "end_mm", PointData(x2Mm, y2Mm) },
                { "height_mm", heightMm },
                { "thickness_mm", thicknessMm }
            };
            if (request.Preview)
            {
                return BridgeResponse.Success("preview", "预览：将创建 1 面墙。", plan);
            }

            WallType targetType;
            Wall created;
            using (Transaction transaction = new Transaction(document, "RCB 创建墙体"))
            {
                transaction.Start();
                targetType = ResolveOrCreateWallType(document, sourceType, effectiveTypeName, thicknessMm);
                created = CreateWall(
                    document,
                    ToXyzAtElevation(x1Mm, y1Mm, level.Elevation),
                    ToXyzAtElevation(x2Mm, y2Mm, level.Elevation),
                    targetType,
                    level,
                    heightMm);
                transaction.Commit();
            }

            plan["wall_type_target"] = targetType.Name;
            plan["id"] = (int)created.Id.Value;
            return BridgeResponse.Success("completed", "已创建墙体。", plan);
        }

        /// <summary>
        /// 底层 Wall.Create 调用封装。 / Low-level Wall.Create call wrapper.
        /// </summary>
        private static Wall CreateWall(Document document, XYZ start, XYZ end, WallType wallType, Level level, double heightMm)
        {
            return Wall.Create(
                document,
                Line.CreateBound(start, end),
                wallType.Id,
                level.Id,
                ToFeet(heightMm),
                0.0,
                false,
                false);
        }

        /// <summary>
        /// 按名称或默认解析标高。 / Resolves a level by name or returns the first available level.
        /// </summary>
        private static Level ResolveLevel(Document document, string requestedName)
        {
            List<Level> levels = new FilteredElementCollector(document)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(candidateLevel => candidateLevel.Elevation)
                .ToList();
            if (levels.Count == 0)
            {
                throw new BridgeCommandException("当前项目没有标高，请先创建标高。");
            }

            if (string.IsNullOrWhiteSpace(requestedName))
            {
                return levels[0];
            }

            Level level = levels.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, requestedName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (level == null)
            {
                throw new BridgeCommandException("找不到标高“" + requestedName + "”。先调用 list_levels 查询。");
            }

            return level;
        }

        /// <summary>
        /// 按名称或默认解析基本墙类型。 / Resolves a wall type by name or returns the first available type.
        /// </summary>
        private static WallType ResolveWallType(Document document, string requestedName)
        {
            List<WallType> types = BasicWallTypes(document).OrderBy(type => type.Name).ToList();
            if (types.Count == 0)
            {
                throw new BridgeCommandException("当前项目没有可用的基本墙类型。");
            }

            if (string.IsNullOrWhiteSpace(requestedName))
            {
                return types[0];
            }

            WallType result = types.FirstOrDefault(type =>
                string.Equals(type.Name, requestedName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (result == null)
            {
                throw new BridgeCommandException("找不到基本墙类型“" + requestedName + "”。先调用 list_wall_types 查询。");
            }

            return result;
        }

        /// <summary>
        /// 查找或创建指定厚度的墙类型。 / Finds or creates a wall type of the specified thickness.
        /// </summary>
        private static WallType ResolveOrCreateWallType(Document document, WallType sourceType, string targetName, double thicknessMm)
        {
            WallType existing = BasicWallTypes(document).FirstOrDefault(type =>
                string.Equals(type.Name, targetName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (Math.Abs(RoundMillimeters(existing.Width) - thicknessMm) > 0.5)
                {
                    throw new BridgeCommandException(
                        "墙类型“" + targetName + "”已存在，但厚度为 " + RoundMillimeters(existing.Width) + "mm，不会改动既有类型。");
                }

                return existing;
            }

            // 复制源墙类型作为新类型的基础
            // Duplicate the source wall type as the base for the new type
            WallType duplicate = sourceType.Duplicate(targetName) as WallType;
            if (duplicate == null)
            {
                throw new BridgeCommandException("无法复制墙类型“" + sourceType.Name + "”。");
            }

            // 调整复合结构的厚度：将厚度差值分配到可变层（或第 0 层）
            // Adjust compound structure thickness: distribute the delta to the variable layer (or layer 0)
            CompoundStructure structure = duplicate.GetCompoundStructure();
            if (structure == null || structure.LayerCount < 1)
            {
                throw new BridgeCommandException("墙类型“" + sourceType.Name + "”没有可调整的复合结构。");
            }

            double currentTotal = structure.GetLayers().Sum(layer => layer.Width);
            double targetTotal = ToFeet(thicknessMm);
            int layerIndex = FindVariableOrFirstLayer(structure);
            double adjustedWidth = structure.GetLayers()[layerIndex].Width + (targetTotal - currentTotal);
            if (adjustedWidth <= 0.0)
            {
                throw new BridgeCommandException("指定墙厚会使墙类型“" + sourceType.Name + "”的层厚无效。");
            }

            structure.SetLayerWidth(layerIndex, adjustedWidth);
            duplicate.SetCompoundStructure(structure);
            return duplicate;
        }

        /// <summary>
        /// 查找复合结构中的可变层，如果没有则返回第 0 层。 / Finds the variable-width layer in a compound structure, or returns layer 0.
        /// </summary>
        private static int FindVariableOrFirstLayer(CompoundStructure structure)
        {
            int variableLayerIndex = structure.VariableLayerIndex;
            return variableLayerIndex >= 0 ? variableLayerIndex : 0;
        }

        /// <summary>
        /// 枚举项目中所有基本墙类型。 / Enumerates all basic wall types in the project.
        /// </summary>
        private static IEnumerable<WallType> BasicWallTypes(Document document)
        {
            return new FilteredElementCollector(document)
                .OfClass(typeof(WallType))
                .Cast<WallType>()
                .Where(type => type.Kind == WallKind.Basic);
        }

        /// <summary>
        /// 将 x/y 毫米坐标转为字典。 / Converts x/y millimeter coordinates to a dictionary.
        /// </summary>
        private static Dictionary<string, object> PointData(double xMm, double yMm)
        {
            return new Dictionary<string, object>
            {
                { "x", xMm },
                { "y", yMm }
            };
        }

        /// <summary>
        /// 将 x/y 毫米坐标转为 Revit XYZ（Z=0）。 / Converts x/y millimeter coordinates to a Revit XYZ (Z=0).
        /// </summary>
        private static XYZ ToXyz(double xMm, double yMm)
        {
            return new XYZ(ToFeet(xMm), ToFeet(yMm), 0.0);
        }

        /// <summary>
        /// 将 x/y 毫米坐标和标高（英尺）转为 Revit XYZ。 / Converts x/y millimeter coordinates and elevation (feet) to a Revit XYZ.
        /// </summary>
        private static XYZ ToXyzAtElevation(double xMm, double yMm, double elevationFeet)
        {
            return new XYZ(ToFeet(xMm), ToFeet(yMm), elevationFeet);
        }

        /// <summary>
        /// 毫米转英尺（Revit 内部单位）。 / Converts millimeters to feet (Revit internal unit).
        /// </summary>
        private static double ToFeet(double millimeters)
        {
            return millimeters * FeetPerMillimeter;
        }

        /// <summary>
        /// 英尺转毫米并四舍五入。 / Converts feet to millimeters and rounds.
        /// </summary>
        private static double RoundMillimeters(double feet)
        {
            return Math.Round(feet / FeetPerMillimeter, 3, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// 确保两点不重合（防止创建零长度图元）。 / Ensures two points are not coincident (prevents zero-length elements).
        /// </summary>
        private static void EnsureDistinctPoints(double x1, double y1, double x2, double y2, string message)
        {
            if (Math.Abs(x1 - x2) < 0.0001 && Math.Abs(y1 - y2) < 0.0001)
            {
                throw new BridgeCommandException(message);
            }
        }

        /// <summary>
        /// 生成墙类型的标准名称。 / Generates a standardized wall type name.
        /// </summary>
        private static string EnsureWallTypeName(WallType sourceType, double thicknessMm)
        {
            return "RCB_" + sourceType.Name + "_" + thicknessMm.ToString("0.###", CultureInfo.InvariantCulture) + "mm";
        }

        /// <summary>
        /// 判断操作是否为写操作。 / Determines whether the operation is a write operation.
        /// </summary>
        private static bool IsWriteOperation(string operation, BridgeRequest request)
        {
            switch (operation)
            {
                case "execute_plan":
                    return PlanCommandExecutor.IsWritePlan(request);
                case "create_level":
                case "create_grid":
                case "create_rectangle_walls":
                case "create_wall":
                case "new_project":
                case "create_family":
                case "load_family":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 将中文操作名标准化为英文操作名。 / Normalizes Chinese operation names to their English equivalents.
        /// </summary>
        private static string NormalizeOperation(string operation)
        {
            string value = (operation ?? string.Empty).Trim().ToLowerInvariant();
            switch (value)
            {
                case "状态":
                case "连接状态":
                    return "health";
                case "查询标高":
                    return "list_levels";
                case "查询墙类型":
                    return "list_wall_types";
                case "新建项目":
                case "创建项目":
                    return "new_project";
                case "执行计划":
                case "执行建模计划":
                    return "execute_plan";
                case "创建标高":
                    return "create_level";
                case "创建轴网":
                    return "create_grid";
                case "创建矩形墙":
                case "创建矩形墙体":
                    return "create_rectangle_walls";
                case "创建墙":
                case "创建墙体":
                    return "create_wall";
                case "查询族样板":
                case "列出族样板":
                    return "list_family_templates";
                case "创建族":
                case "新建族":
                    return "create_family";
                case "载入族":
                case "加载族":
                    return "load_family";
                default:
                    return value;
            }
        }
    }
}
