using System.Text;
using System.Text.Json;
using UndertaleModLib;
using UndertaleModLib.Models;

namespace G3MToolCLI.Services;

public static partial class ResourceImportService
{
    private static bool TryGetUInt32(JsonElement element, out uint value)
    {
        if (element.ValueKind != JsonValueKind.Number)
        {
            value = 0;
            return false;
        }

        if (element.TryGetUInt32(out value))
            return true;

        if (element.TryGetInt64(out long signed))
        {
            value = unchecked((uint)signed);
            return true;
        }

        if (element.TryGetDouble(out double dbl))
        {
            value = unchecked((uint)dbl);
            return true;
        }

        value = 0;
        return false;
    }

    private static bool SupportsLegacyRoomTiles(UndertaleData data)
    {
        uint major = data.GeneralInfo?.Major ?? 0;
        return major < 2023;
    }

    // =========================================================================
    // Rooms
    // =========================================================================
    private static void ImportRooms(UndertaleData data, string inputDir)
    {
        var roomDirs = GetDirs(inputDir);
        if (roomDirs.Length == 0) return;

        // Read RoomOrder from patch GeneralInfo to determine order for new rooms
        var patchRoomOrder = new List<string>();
        string giPath = Path.Combine(Path.GetDirectoryName(inputDir)!, "GeneralInfo", "GeneralInfo.json");
        if (FExists(giPath))
        {
            try
            {
                using var giDoc = JsonDocument.Parse(FReadText(giPath));
                if (giDoc.RootElement.TryGetProperty("roomOrder", out JsonElement roElm))
                    foreach (var e in roElm.EnumerateArray())
                        patchRoomOrder.Add(e.GetString() ?? "");
            }
            catch { }
        }

        var sorted = roomDirs.OrderBy(d =>
        {
            int idx = patchRoomOrder.IndexOf(Path.GetFileName(d));
            return idx >= 0 ? idx : int.MaxValue;
        }).ToArray();

        Log($"[ImportRooms] Found {sorted.Length} room folder(s) to import.");

        foreach (string roomDir in sorted)
        {
            string roomFile = Path.Combine(roomDir, "room.json");
            if (!FExists(roomFile)) continue;

            try
            {
                string roomName = Path.GetFileName(roomDir);
                using var jsonDoc = JsonDocument.Parse(FReadText(roomFile));
                var root = jsonDoc.RootElement;

                var room = data.Rooms.ByName(roomName);
                if (room == null)
                {
                    room = new UndertaleRoom { Name = data.Strings.MakeString(roomName) };
                    data.Rooms.Add(room);
                    data.GeneralInfo?.RoomOrder?.Add(
                        new UndertaleResourceById<UndertaleRoom, UndertaleChunkROOM> { Resource = room });
                }

                UpdateRoomFromJson(data, room, root);
            }
            catch (Exception ex)
            {
                Log($"[ImportRooms] Error: {Path.GetFileName(roomDir)}: {ex.Message}");
            }
        }
        Log("[ImportRooms] Done.");
    }

    private static void UpdateRoomFromJson(UndertaleData data, UndertaleRoom room, JsonElement d)
    {
        if (d.TryGetProperty("caption", out var captionElm) && captionElm.ValueKind == JsonValueKind.String)
            room.Caption = data.Strings.MakeString(captionElm.GetString()!);
        if (d.TryGetProperty("width", out var wElm) && wElm.ValueKind == JsonValueKind.Number)
            room.Width = (uint)Math.Max(0, wElm.GetInt32());
        if (d.TryGetProperty("height", out var hElm) && hElm.ValueKind == JsonValueKind.Number)
            room.Height = (uint)Math.Max(0, hElm.GetInt32());
        if (d.TryGetProperty("speed", out var spElm) && spElm.ValueKind == JsonValueKind.Number)
            room.Speed = (uint)Math.Max(0, spElm.GetInt32());
        if (d.TryGetProperty("persistent", out var perElm) && (perElm.ValueKind == JsonValueKind.True || perElm.ValueKind == JsonValueKind.False))
            room.Persistent = perElm.GetBoolean();
        if (d.TryGetProperty("backgroundColor", out var bgcElm) && TryGetUInt32(bgcElm, out uint backgroundColor))
            room.BackgroundColor = backgroundColor;
        if (d.TryGetProperty("drawBackgroundColor", out var dbcElm) && (dbcElm.ValueKind == JsonValueKind.True || dbcElm.ValueKind == JsonValueKind.False))
            room.DrawBackgroundColor = dbcElm.GetBoolean();
        if (d.TryGetProperty("creationCodeId", out var ccElm) && ccElm.ValueKind == JsonValueKind.String)
        {
            string cn = ccElm.GetString() ?? "";
            if (cn.Length > 0)
            {
                var code = data.Code.ByName(cn);
                if (code == null)
                {
                    code = new UndertaleCode { Name = data.Strings.MakeString(cn) };
                    data.Code.Add(code);
                    var cl = new UndertaleCodeLocals { Name = code.Name };
                    data.CodeLocals.Add(cl);
                    code.LocalsCount = 0;
                }
                room.CreationCodeId = code;
            }
        }
        if (d.TryGetProperty("flags", out var flElm) && flElm.ValueKind == JsonValueKind.Number)
            room.Flags = (UndertaleRoom.RoomEntryFlags)flElm.GetInt32();
        if (d.TryGetProperty("world", out var wrElm) && (wrElm.ValueKind == JsonValueKind.True || wrElm.ValueKind == JsonValueKind.False))
            room.World = wrElm.GetBoolean();
        if (d.TryGetProperty("top", out var topElm) && topElm.ValueKind == JsonValueKind.Number)
            room.Top = (uint)Math.Max(0, topElm.GetInt32());
        if (d.TryGetProperty("left", out var leftElm) && leftElm.ValueKind == JsonValueKind.Number)
            room.Left = (uint)Math.Max(0, leftElm.GetInt32());
        if (d.TryGetProperty("right", out var rightElm) && rightElm.ValueKind == JsonValueKind.Number)
            room.Right = (uint)Math.Max(0, rightElm.GetInt32());
        if (d.TryGetProperty("bottom", out var bottomElm) && bottomElm.ValueKind == JsonValueKind.Number)
            room.Bottom = (uint)Math.Max(0, bottomElm.GetInt32());
        if (d.TryGetProperty("gravityX", out var gxElm) && gxElm.ValueKind == JsonValueKind.Number)
            room.GravityX = (float)gxElm.GetDouble();
        if (d.TryGetProperty("gravityY", out var gyElm) && gyElm.ValueKind == JsonValueKind.Number)
            room.GravityY = (float)gyElm.GetDouble();
        if (d.TryGetProperty("metersPerPixel", out var mppElm) && mppElm.ValueKind == JsonValueKind.Number)
            room.MetersPerPixel = (float)mppElm.GetDouble();
        if (d.TryGetProperty("gridWidth", out var gwElm) && gwElm.ValueKind == JsonValueKind.Number)
            room.GridWidth = gwElm.GetDouble();
        if (d.TryGetProperty("gridHeight", out var ghElm) && ghElm.ValueKind == JsonValueKind.Number)
            room.GridHeight = ghElm.GetDouble();
        if (d.TryGetProperty("gridThicknessPx", out var gtElm) && gtElm.ValueKind == JsonValueKind.Number)
            room.GridThicknessPx = gtElm.GetDouble();

        // Backgrounds
        if (d.TryGetProperty("backgrounds", out var bgsElm) && bgsElm.ValueKind == JsonValueKind.Array)
        {
            room.Backgrounds.Clear();
            foreach (var bgElm in bgsElm.EnumerateArray())
            {
                var bg = new UndertaleRoom.Background { ParentRoom = room };
                if (bgElm.TryGetProperty("enabled", out var enElm) && (enElm.ValueKind == JsonValueKind.True || enElm.ValueKind == JsonValueKind.False))
                    bg.Enabled = enElm.GetBoolean();
                if (bgElm.TryGetProperty("foreground", out var fgElm) && (fgElm.ValueKind == JsonValueKind.True || fgElm.ValueKind == JsonValueKind.False))
                    bg.Foreground = fgElm.GetBoolean();
                if (bgElm.TryGetProperty("backgroundDefinition", out var bdElm) && bdElm.ValueKind == JsonValueKind.String)
                {
                    string bn = bdElm.GetString() ?? "";
                    if (bn.Length > 0) { var bgDef = data.Backgrounds.ByName(bn); if (bgDef != null) bg.BackgroundDefinition = bgDef; }
                }
                if (bgElm.TryGetProperty("x", out var xElm) && xElm.ValueKind == JsonValueKind.Number) bg.X = xElm.GetInt32();
                if (bgElm.TryGetProperty("y", out var yElm) && yElm.ValueKind == JsonValueKind.Number) bg.Y = yElm.GetInt32();
                if (bgElm.TryGetProperty("tiledHorizontally", out var thElm) && (thElm.ValueKind == JsonValueKind.True || thElm.ValueKind == JsonValueKind.False))
                    bg.TiledHorizontally = thElm.GetBoolean();
                if (bgElm.TryGetProperty("tiledVertically", out var tvElm) && (tvElm.ValueKind == JsonValueKind.True || tvElm.ValueKind == JsonValueKind.False))
                    bg.TiledVertically = tvElm.GetBoolean();
                if (bgElm.TryGetProperty("speedX", out var sxElm) && sxElm.ValueKind == JsonValueKind.Number) bg.SpeedX = sxElm.GetInt32();
                if (bgElm.TryGetProperty("speedY", out var syElm) && syElm.ValueKind == JsonValueKind.Number) bg.SpeedY = syElm.GetInt32();
                if (bgElm.TryGetProperty("stretch", out var stElm) && (stElm.ValueKind == JsonValueKind.True || stElm.ValueKind == JsonValueKind.False))
                    bg.Stretch = stElm.GetBoolean();
                room.Backgrounds.Add(bg);
            }
        }

        // Views
        if (d.TryGetProperty("views", out var viewsElm) && viewsElm.ValueKind == JsonValueKind.Array)
        {
            room.Views.Clear();
            foreach (var vElm in viewsElm.EnumerateArray())
            {
                var view = new UndertaleRoom.View();
                if (vElm.TryGetProperty("enabled", out var enElm) && (enElm.ValueKind == JsonValueKind.True || enElm.ValueKind == JsonValueKind.False))
                    view.Enabled = enElm.GetBoolean();
                if (vElm.TryGetProperty("viewX", out var vxElm) && vxElm.ValueKind == JsonValueKind.Number) view.ViewX = vxElm.GetInt32();
                if (vElm.TryGetProperty("viewY", out var vyElm) && vyElm.ValueKind == JsonValueKind.Number) view.ViewY = vyElm.GetInt32();
                if (vElm.TryGetProperty("viewWidth", out var vwElm) && vwElm.ValueKind == JsonValueKind.Number) view.ViewWidth = vwElm.GetInt32();
                if (vElm.TryGetProperty("viewHeight", out var vhElm) && vhElm.ValueKind == JsonValueKind.Number) view.ViewHeight = vhElm.GetInt32();
                if (vElm.TryGetProperty("portX", out var pxElm) && pxElm.ValueKind == JsonValueKind.Number) view.PortX = pxElm.GetInt32();
                if (vElm.TryGetProperty("portY", out var pyElm) && pyElm.ValueKind == JsonValueKind.Number) view.PortY = pyElm.GetInt32();
                if (vElm.TryGetProperty("portWidth", out var pwElm) && pwElm.ValueKind == JsonValueKind.Number) view.PortWidth = pwElm.GetInt32();
                if (vElm.TryGetProperty("portHeight", out var phElm) && phElm.ValueKind == JsonValueKind.Number) view.PortHeight = phElm.GetInt32();
                if (vElm.TryGetProperty("borderX", out var bxElm) && TryGetUInt32(bxElm, out uint borderX)) view.BorderX = borderX;
                if (vElm.TryGetProperty("borderY", out var byElm) && TryGetUInt32(byElm, out uint borderY)) view.BorderY = borderY;
                if (vElm.TryGetProperty("speedX", out var sxElm) && sxElm.ValueKind == JsonValueKind.Number) view.SpeedX = sxElm.GetInt32();
                if (vElm.TryGetProperty("speedY", out var syElm) && syElm.ValueKind == JsonValueKind.Number) view.SpeedY = syElm.GetInt32();
                if (vElm.TryGetProperty("objectId", out var oiElm) && oiElm.ValueKind == JsonValueKind.String)
                {
                    string on = oiElm.GetString() ?? "";
                    if (on.Length > 0) { var obj = data.GameObjects.ByName(on); if (obj != null) view.ObjectId = obj; }
                }
                room.Views.Add(view);
            }
        }

        // GameObjects (instances)
        if (d.TryGetProperty("gameObjects", out var gosElm) && gosElm.ValueKind == JsonValueKind.Array)
        {
            room.GameObjects.Clear();
            foreach (var oElm in gosElm.EnumerateArray())
            {
                var go = new UndertaleRoom.GameObject();
                if (oElm.TryGetProperty("x", out var xElm) && xElm.ValueKind == JsonValueKind.Number) go.X = xElm.GetInt32();
                if (oElm.TryGetProperty("y", out var yElm) && yElm.ValueKind == JsonValueKind.Number) go.Y = yElm.GetInt32();
                if (oElm.TryGetProperty("objectDefinition", out var odElm) && odElm.ValueKind == JsonValueKind.String)
                {
                    string on = odElm.GetString() ?? "";
                    if (on.Length > 0) { var od = data.GameObjects.ByName(on); if (od != null) go.ObjectDefinition = od; }
                }
                if (oElm.TryGetProperty("instanceID", out var iiElm) && TryGetUInt32(iiElm, out uint instanceId))
                    go.InstanceID = instanceId;
                if (oElm.TryGetProperty("creationCode", out var ccElm2) && ccElm2.ValueKind == JsonValueKind.String)
                {
                    string cn = ccElm2.GetString() ?? "";
                    if (cn.Length > 0) go.CreationCode = EnsureCodeEntry(data, cn);
                }
                if (oElm.TryGetProperty("scaleX", out var sxElm) && sxElm.ValueKind == JsonValueKind.Number) go.ScaleX = (float)sxElm.GetDouble();
                if (oElm.TryGetProperty("scaleY", out var syElm) && syElm.ValueKind == JsonValueKind.Number) go.ScaleY = (float)syElm.GetDouble();
                if (oElm.TryGetProperty("color", out var colElm) && TryGetUInt32(colElm, out uint gameObjectColor)) go.Color = gameObjectColor;
                if (oElm.TryGetProperty("rotation", out var rotElm) && rotElm.ValueKind == JsonValueKind.Number) go.Rotation = (float)rotElm.GetDouble();
                if (oElm.TryGetProperty("preCreateCode", out var pcElm) && pcElm.ValueKind == JsonValueKind.String)
                {
                    string pcn = pcElm.GetString() ?? "";
                    if (pcn.Length > 0) go.PreCreateCode = EnsureCodeEntry(data, pcn);
                }
                if (data.IsVersionAtLeast(2, 2, 2, 302))
                {
                    if (oElm.TryGetProperty("imageSpeed", out var isElm) && isElm.ValueKind == JsonValueKind.Number)
                        go.ImageSpeed = (float)isElm.GetDouble();
                    if (oElm.TryGetProperty("imageIndex", out var ixElm) && ixElm.ValueKind == JsonValueKind.Number)
                        go.ImageIndex = ixElm.GetInt32();
                }
                room.GameObjects.Add(go);
            }
        }

        // Tiles
        if (d.TryGetProperty("tiles", out var tilesElm) && tilesElm.ValueKind == JsonValueKind.Array)
        {
            room.Tiles.Clear();
            foreach (var tElm in tilesElm.EnumerateArray())
            {
                var tile = new UndertaleRoom.Tile();
                if (tElm.TryGetProperty("x", out var xElm) && xElm.ValueKind == JsonValueKind.Number) tile.X = xElm.GetInt32();
                if (tElm.TryGetProperty("y", out var yElm) && yElm.ValueKind == JsonValueKind.Number) tile.Y = yElm.GetInt32();
                if (tElm.TryGetProperty("spriteMode", out var smElm) && (smElm.ValueKind == JsonValueKind.True || smElm.ValueKind == JsonValueKind.False))
                    tile.spriteMode = smElm.GetBoolean();
                if (tile.spriteMode)
                {
                    if (tElm.TryGetProperty("spriteDefinition", out var sdElm) && sdElm.ValueKind == JsonValueKind.String)
                    {
                        string sn = sdElm.GetString() ?? "";
                        if (sn.Length > 0) { var spr = data.Sprites.ByName(sn); if (spr != null) tile.SpriteDefinition = spr; }
                    }
                }
                else
                {
                    if (tElm.TryGetProperty("backgroundDefinition", out var bdElm) && bdElm.ValueKind == JsonValueKind.String)
                    {
                        string bn = bdElm.GetString() ?? "";
                        if (bn.Length > 0) { var bg = data.Backgrounds.ByName(bn); if (bg != null) tile.BackgroundDefinition = bg; }
                    }
                }
                if (tElm.TryGetProperty("sourceX", out var sxElm) && sxElm.ValueKind == JsonValueKind.Number) tile.SourceX = sxElm.GetInt32();
                if (tElm.TryGetProperty("sourceY", out var syElm) && syElm.ValueKind == JsonValueKind.Number) tile.SourceY = syElm.GetInt32();
                if (tElm.TryGetProperty("width", out var wElm2) && TryGetUInt32(wElm2, out uint tileWidth)) tile.Width = tileWidth;
                if (tElm.TryGetProperty("height", out var hElm2) && TryGetUInt32(hElm2, out uint tileHeight)) tile.Height = tileHeight;
                if (tElm.TryGetProperty("tileDepth", out var tdElm) && tdElm.ValueKind == JsonValueKind.Number) tile.TileDepth = tdElm.GetInt32();
                if (tElm.TryGetProperty("instanceID", out var iiElm2) && TryGetUInt32(iiElm2, out uint tileInstanceId)) tile.InstanceID = tileInstanceId;
                if (tElm.TryGetProperty("scaleX", out var scxElm) && scxElm.ValueKind == JsonValueKind.Number) tile.ScaleX = (float)scxElm.GetDouble();
                if (tElm.TryGetProperty("scaleY", out var scyElm) && scyElm.ValueKind == JsonValueKind.Number) tile.ScaleY = (float)scyElm.GetDouble();
                if (tElm.TryGetProperty("color", out var colElm2) && TryGetUInt32(colElm2, out uint tileColor)) tile.Color = tileColor;
                room.Tiles.Add(tile);
            }
        }

        // Layers (GMS2)
        if (data.IsGameMaker2() && d.TryGetProperty("layers", out var layersElm) && layersElm.ValueKind == JsonValueKind.Array)
        {
            room.Layers.Clear();
            foreach (var lElm in layersElm.EnumerateArray())
            {
                if (!lElm.TryGetProperty("layerType", out var ltElm) || ltElm.ValueKind != JsonValueKind.Number) continue;
                int lt = ltElm.GetInt32();
                var layer = new UndertaleRoom.Layer
                {
                    LayerType = (UndertaleRoom.LayerType)lt,
                    ParentRoom = room
                };

                if (lElm.TryGetProperty("layerName", out var lnElm) && lnElm.ValueKind == JsonValueKind.String)
                    layer.LayerName = data.Strings.MakeString(lnElm.GetString()!);
                if (lElm.TryGetProperty("layerId", out var liElm) && TryGetUInt32(liElm, out uint layerId))
                    layer.LayerId = layerId;
                if (lElm.TryGetProperty("layerDepth", out var ldElm) && ldElm.ValueKind == JsonValueKind.Number)
                    layer.LayerDepth = ldElm.GetInt32();
                if (lElm.TryGetProperty("xOffset", out var xoElm) && xoElm.ValueKind == JsonValueKind.Number)
                    layer.XOffset = (float)xoElm.GetDouble();
                if (lElm.TryGetProperty("yOffset", out var yoElm) && yoElm.ValueKind == JsonValueKind.Number)
                    layer.YOffset = (float)yoElm.GetDouble();
                if (lElm.TryGetProperty("hSpeed", out var hsElm) && hsElm.ValueKind == JsonValueKind.Number)
                    layer.HSpeed = (float)hsElm.GetDouble();
                if (lElm.TryGetProperty("vSpeed", out var vsElm) && vsElm.ValueKind == JsonValueKind.Number)
                    layer.VSpeed = (float)vsElm.GetDouble();
                if (lElm.TryGetProperty("isVisible", out var ivElm) && (ivElm.ValueKind == JsonValueKind.True || ivElm.ValueKind == JsonValueKind.False))
                    layer.IsVisible = ivElm.GetBoolean();
                if (data.IsVersionAtLeast(2022, 1))
                {
                    if (lElm.TryGetProperty("effectEnabled", out var eeElm) && (eeElm.ValueKind == JsonValueKind.True || eeElm.ValueKind == JsonValueKind.False))
                        layer.EffectEnabled = eeElm.GetBoolean();
                    if (lElm.TryGetProperty("effectType", out var etElm) && etElm.ValueKind == JsonValueKind.String)
                        layer.EffectType = data.Strings.MakeString(etElm.GetString()!);
                }

                if (lt == (int)UndertaleRoom.LayerType.Instances)
                {
                    var instData = new UndertaleRoom.Layer.LayerInstancesData();
                    if (lElm.TryGetProperty("instanceIds", out var idsElm) && idsElm.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var idElm in idsElm.EnumerateArray())
                        {
                            if (!TryGetUInt32(idElm, out uint iid)) continue;
                            var go = room.GameObjects.FirstOrDefault(g => g.InstanceID == iid);
                            if (go != null) instData.Instances.Add(go);
                        }
                    }
                    layer.Data = instData;
                }
                else if (lt == (int)UndertaleRoom.LayerType.Tiles)
                {
                    var tilesData = new UndertaleRoom.Layer.LayerTilesData { ParentLayer = layer };
                    if (lElm.TryGetProperty("tilesBackground", out var tbElm) && tbElm.ValueKind == JsonValueKind.String)
                    {
                        string bn = tbElm.GetString() ?? "";
                        if (bn.Length > 0) { var bg = data.Backgrounds.ByName(bn); if (bg != null) tilesData.Background = bg; }
                    }
                    if (lElm.TryGetProperty("tilesX", out var txElm) && TryGetUInt32(txElm, out uint tilesX))
                        tilesData.TilesX = tilesX;
                    if (lElm.TryGetProperty("tilesY", out var tyElm) && TryGetUInt32(tyElm, out uint tilesY))
                        tilesData.TilesY = tilesY;
                    if (lElm.TryGetProperty("tileData", out var tdElm) && tdElm.ValueKind == JsonValueKind.Array)
                    {
                        var rows = new List<uint[]>();
                        foreach (var rowElm in tdElm.EnumerateArray())
                        {
                            if (rowElm.ValueKind != JsonValueKind.Array) continue;
                            rows.Add([.. rowElm.EnumerateArray()
                                .Where(c => c.ValueKind == JsonValueKind.Number)
                                .Select(c => (uint)c.GetInt32())]);
                        }
                        tilesData.TileData = [.. rows];
                    }
                    layer.Data = tilesData;
                }
                else if (lt == (int)UndertaleRoom.LayerType.Background)
                {
                    var bgData = new UndertaleRoom.Layer.LayerBackgroundData { ParentLayer = layer };
                    if (lElm.TryGetProperty("backgroundData", out var bdElm) && bdElm.ValueKind == JsonValueKind.Object)
                    {
                        if (bdElm.TryGetProperty("visible", out var vElm) && (vElm.ValueKind == JsonValueKind.True || vElm.ValueKind == JsonValueKind.False))
                            bgData.Visible = vElm.GetBoolean();
                        if (bdElm.TryGetProperty("foreground", out var fElm) && (fElm.ValueKind == JsonValueKind.True || fElm.ValueKind == JsonValueKind.False))
                            bgData.Foreground = fElm.GetBoolean();
                        if (bdElm.TryGetProperty("sprite", out var sElm) && sElm.ValueKind == JsonValueKind.String)
                        {
                            string sn = sElm.GetString() ?? "";
                            if (sn.Length > 0) { var spr = data.Sprites.ByName(sn); if (spr != null) bgData.Sprite = spr; }
                        }
                        if (bdElm.TryGetProperty("tiledHorizontally", out var thElm) && (thElm.ValueKind == JsonValueKind.True || thElm.ValueKind == JsonValueKind.False))
                            bgData.TiledHorizontally = thElm.GetBoolean();
                        if (bdElm.TryGetProperty("tiledVertically", out var tvElm) && (tvElm.ValueKind == JsonValueKind.True || tvElm.ValueKind == JsonValueKind.False))
                            bgData.TiledVertically = tvElm.GetBoolean();
                        if (bdElm.TryGetProperty("stretch", out var stElm) && (stElm.ValueKind == JsonValueKind.True || stElm.ValueKind == JsonValueKind.False))
                            bgData.Stretch = stElm.GetBoolean();
                        if (bdElm.TryGetProperty("color", out var cElm) && TryGetUInt32(cElm, out uint backgroundLayerColor))
                            bgData.Color = backgroundLayerColor;
                        if (bdElm.TryGetProperty("firstFrame", out var ffElm) && ffElm.ValueKind == JsonValueKind.Number)
                            bgData.FirstFrame = (float)ffElm.GetDouble();
                        if (bdElm.TryGetProperty("animationSpeed", out var asElm) && asElm.ValueKind == JsonValueKind.Number)
                            bgData.AnimationSpeed = (float)asElm.GetDouble();
                        if (bdElm.TryGetProperty("animationSpeedType", out var astElm) && astElm.ValueKind == JsonValueKind.Number)
                            bgData.AnimationSpeedType = (AnimationSpeedType)astElm.GetInt32();
                    }
                    layer.Data = bgData;
                }
                else if (lt == (int)UndertaleRoom.LayerType.Assets)
                {
                    var assetsData = new UndertaleRoom.Layer.LayerAssetsData
                    {
                        LegacyTiles = [],
                        Sprites = []
                    };
                    if (data.IsVersionAtLeast(2, 3))
                        assetsData.Sequences = [];

                    if (lElm.TryGetProperty("assetsData", out var adElm) && adElm.ValueKind == JsonValueKind.Object)
                    {
                        bool supportsLegacy = SupportsLegacyRoomTiles(data);
                        if (supportsLegacy && adElm.TryGetProperty("legacyTiles", out var ltElm2) && ltElm2.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var tElm in ltElm2.EnumerateArray())
                            {
                                var tile = new UndertaleRoom.Tile();
                                if (tElm.TryGetProperty("x", out var xElm) && xElm.ValueKind == JsonValueKind.Number) tile.X = xElm.GetInt32();
                                if (tElm.TryGetProperty("y", out var yElm) && yElm.ValueKind == JsonValueKind.Number) tile.Y = yElm.GetInt32();
                                if (tElm.TryGetProperty("sourceX", out var sxElm) && sxElm.ValueKind == JsonValueKind.Number) tile.SourceX = sxElm.GetInt32();
                                if (tElm.TryGetProperty("sourceY", out var syElm) && syElm.ValueKind == JsonValueKind.Number) tile.SourceY = syElm.GetInt32();
                                if (tElm.TryGetProperty("width", out var wElm2) && TryGetUInt32(wElm2, out uint legacyWidth)) tile.Width = legacyWidth;
                                if (tElm.TryGetProperty("height", out var hElm2) && TryGetUInt32(hElm2, out uint legacyHeight)) tile.Height = legacyHeight;
                                if (tElm.TryGetProperty("tileDepth", out var dElm) && dElm.ValueKind == JsonValueKind.Number) tile.TileDepth = dElm.GetInt32();
                                if (tElm.TryGetProperty("instanceID", out var iiElm) && TryGetUInt32(iiElm, out uint legacyInstanceId)) tile.InstanceID = legacyInstanceId;
                                if (tElm.TryGetProperty("scaleX", out var scxElm) && scxElm.ValueKind == JsonValueKind.Number) tile.ScaleX = (float)scxElm.GetDouble();
                                if (tElm.TryGetProperty("scaleY", out var scyElm) && scyElm.ValueKind == JsonValueKind.Number) tile.ScaleY = (float)scyElm.GetDouble();
                                if (tElm.TryGetProperty("color", out var colElm) && TryGetUInt32(colElm, out uint legacyTileColor)) tile.Color = legacyTileColor;
                                if (tElm.TryGetProperty("background", out var bgElm) && bgElm.ValueKind == JsonValueKind.String)
                                {
                                    string bn = bgElm.GetString() ?? "";
                                    if (bn.Length > 0) { var bg = data.Backgrounds.ByName(bn); if (bg != null) tile.BackgroundDefinition = bg; }
                                }
                                assetsData.LegacyTiles.Add(tile);
                            }
                        }

                        if (adElm.TryGetProperty("sprites", out var sprElm) && sprElm.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var sElm in sprElm.EnumerateArray())
                            {
                                var si = new UndertaleRoom.SpriteInstance();
                                if (sElm.TryGetProperty("name", out var nElm) && nElm.ValueKind == JsonValueKind.String)
                                    si.Name = data.Strings.MakeString(nElm.GetString()!);
                                if (sElm.TryGetProperty("sprite", out var srElm) && srElm.ValueKind == JsonValueKind.String)
                                {
                                    string sn = srElm.GetString() ?? "";
                                    if (sn.Length > 0) { var spr = data.Sprites.ByName(sn); if (spr != null) si.Sprite = spr; }
                                }
                                if (sElm.TryGetProperty("x", out var xElm) && xElm.ValueKind == JsonValueKind.Number) si.X = xElm.GetInt32();
                                if (sElm.TryGetProperty("y", out var yElm) && yElm.ValueKind == JsonValueKind.Number) si.Y = yElm.GetInt32();
                                if (sElm.TryGetProperty("scaleX", out var sxElm) && sxElm.ValueKind == JsonValueKind.Number) si.ScaleX = (float)sxElm.GetDouble();
                                if (sElm.TryGetProperty("scaleY", out var syElm) && syElm.ValueKind == JsonValueKind.Number) si.ScaleY = (float)syElm.GetDouble();
                                if (sElm.TryGetProperty("color", out var colElm) && TryGetUInt32(colElm, out uint spriteColor)) si.Color = spriteColor;
                                if (sElm.TryGetProperty("animationSpeed", out var asElm) && asElm.ValueKind == JsonValueKind.Number)
                                    si.AnimationSpeed = (float)asElm.GetDouble();
                                if (sElm.TryGetProperty("animationSpeedType", out var astElm) && astElm.ValueKind == JsonValueKind.Number)
                                    si.AnimationSpeedType = (AnimationSpeedType)astElm.GetInt32();
                                if (sElm.TryGetProperty("frameIndex", out var fiElm) && fiElm.ValueKind == JsonValueKind.Number)
                                    si.FrameIndex = (float)fiElm.GetDouble();
                                if (sElm.TryGetProperty("rotation", out var rotElm) && rotElm.ValueKind == JsonValueKind.Number)
                                    si.Rotation = (float)rotElm.GetDouble();
                                assetsData.Sprites.Add(si);
                            }
                        }
                    }
                    layer.Data = assetsData;
                }
                else if (lt == (int)UndertaleRoom.LayerType.Effect)
                {
                    layer.Data = new UndertaleRoom.Layer.LayerEffectData();
                }

                room.Layers.Add(layer);
            }
        }

        // Sequences
        if (data.IsVersionAtLeast(2, 3) && d.TryGetProperty("sequences", out var seqsElm) && seqsElm.ValueKind == JsonValueKind.Array)
        {
            room.Sequences.Clear();
            foreach (var sElm in seqsElm.EnumerateArray())
            {
                if (sElm.ValueKind != JsonValueKind.String) continue;
                string sn = sElm.GetString() ?? "";
                if (sn.Length == 0) continue;
                var seq = data.Sequences.ByName(sn);
                if (seq != null)
                    room.Sequences.Add(new UndertaleResourceById<UndertaleSequence, UndertaleChunkSEQN> { Resource = seq });
            }
        }

        // Instance creation order IDs
        if (data.IsVersionAtLeast(2024, 13) && d.TryGetProperty("instanceCreationOrderIDs", out var icoElm) && icoElm.ValueKind == JsonValueKind.Array)
        {
            room.InstanceCreationOrderIDs ??= new UndertaleRoom.InstanceIDList();
            room.InstanceCreationOrderIDs.InstanceIDs.Clear();
            foreach (var idElm in icoElm.EnumerateArray())
                if (idElm.ValueKind == JsonValueKind.Number)
                    room.InstanceCreationOrderIDs.InstanceIDs.Add(idElm.GetInt32());
        }
    }

    private static UndertaleCode EnsureCodeEntry(UndertaleData data, string codeName)
    {
        var code = data.Code.ByName(codeName);
        if (code == null)
        {
            code = new UndertaleCode { Name = data.Strings.MakeString(codeName) };
            data.Code.Add(code);
            var cl = new UndertaleCodeLocals { Name = code.Name };
            data.CodeLocals.Add(cl);
            code.LocalsCount = 0;
        }
        return code;
    }
}
