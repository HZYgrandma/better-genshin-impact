﻿using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OpenCv;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Common.Map;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.GameTask.QuickTeleport.Assets;
using BetterGenshinImpact.Helpers.Extensions;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.Common.Exceptions;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoTrackPath;

/// <summary>
/// 传送任务
/// </summary>
public class TpTask(CancellationToken ct)
{
    private readonly QuickTeleportAssets _assets = QuickTeleportAssets.Instance;
    private readonly Rect _captureRect = TaskContext.Instance().SystemInfo.ScaleMax1080PCaptureRect;
    private readonly double _zoomOutMax1080PRatio = TaskContext.Instance().SystemInfo.ZoomOutMax1080PRatio;

    public static double ReviveStatueOfTheSevenPointX = 2296.4;
    public static double ReviveStatueOfTheSevenPointY = -824.4;

    /// <summary>
    /// 通过大地图传送到指定坐标最近的传送点，然后移动到指定坐标
    /// </summary>
    /// <param name="tpX"></param>
    /// <param name="tpY"></param>
    /// <param name="force">强制以当前的tpX,tpY坐标进行自动传送</param>
    public async Task<(double, double)> TpOnce(double tpX, double tpY, bool force = false)
    {
        var (x, y) = (tpX, tpY);

        if (!force)
        {
            // 获取最近的传送点位置
            (x, y) = GetRecentlyTpPoint(tpX, tpY);
            Logger.LogDebug("({TpX},{TpY}) 最近的传送点位置 ({X},{Y})", $"{tpX:F1}", $"{tpY:F1}", $"{x:F1}", $"{y:F1}");
        }

        // M 打开地图识别当前位置，中心点为当前位置
        using var ra1 = CaptureToRectArea();
        if (!Bv.IsInBigMapUi(ra1))
        {
            Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_M);
            await Delay(1000, ct);
        }

        // 计算传送点位置离哪个地图切换后的中心点最近，切换到该地图
        await SwitchRecentlyCountryMap(x, y);

        // 计算坐标后点击
        var bigMapInAllMapRect = GetBigMapRect();
        while (!IsPointInBigMapWindow(bigMapInAllMapRect, x, y)) // 左上角 350x400也属于禁止点击区域
        {
            Debug.WriteLine($"({x},{y}) 不在 {bigMapInAllMapRect} 内，继续移动");
            Logger.LogInformation("传送点不在当前大地图范围内，继续移动");
            await MoveMapTo(x, y);
            await Delay(300, ct); // 等待地图移动完成
            bigMapInAllMapRect = GetBigMapRect();
        }

        // Debug.WriteLine($"({x},{y}) 在 {bigMapInAllMapRect} 内，计算它在窗体内的位置");
        // 注意这个坐标的原点是中心区域某个点，所以要转换一下点击坐标（点击坐标是左上角为原点的坐标系），不能只是缩放
        var (clickX, clickY) = ConvertToGameRegionPosition(bigMapInAllMapRect, x, y);
        Logger.LogInformation("点击传送点");
        using var ra = CaptureToRectArea();
        ra.ClickTo((int)clickX, (int)clickY);

        // 触发一次快速传送功能
        await Delay(500, ct);
        await ClickTpPoint(CaptureToRectArea());

        // 等待传送完成
        for (var i = 0; i < 20; i++)
        {
            await Delay(1200, ct);
            using var ra3 = CaptureToRectArea();
            if (Bv.IsInMainUi(ra3))
            {
                break;
            }
        }

        Logger.LogInformation("传送完成");
        return (x, y);
    }

    /// <summary>
    /// 传送点是否在大地图窗口内
    /// </summary>
    /// <param name="bigMapInAllMapRect">大地图在整个游戏地图中的矩形位置（原神坐标系）</param>
    /// <param name="x">传送点x坐标（原神坐标系）</param>
    /// <param name="y">传送点y坐标（原神坐标系）</param>
    /// <returns></returns>
    private bool IsPointInBigMapWindow(Rect bigMapInAllMapRect, double x, double y)
    {
        // 坐标不包含直接返回
        if (!bigMapInAllMapRect.Contains(x, y))
        {
            return false;
        }

        var (clickX, clickY) = ConvertToGameRegionPosition(bigMapInAllMapRect, x, y);
        // 屏蔽左上角350x400区域
        if (clickX < 350 * _zoomOutMax1080PRatio && clickY < 400 * _zoomOutMax1080PRatio)
        {
            return false;
        }

        // 屏蔽周围 115 一圈的区域
        if (clickX < 115 * _zoomOutMax1080PRatio
            || clickY < 115 * _zoomOutMax1080PRatio
            || clickX > _captureRect.Width - 115 * _zoomOutMax1080PRatio
            || clickY > _captureRect.Height - 115 * _zoomOutMax1080PRatio)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// 转换传送点坐标到窗体内需要点击的坐标
    /// </summary>
    /// <param name="bigMapInAllMapRect">大地图在整个游戏地图中的矩形位置（原神坐标系）</param>
    /// <param name="x">传送点x坐标（原神坐标系）</param>
    /// <param name="y">传送点y坐标（原神坐标系）</param>
    /// <returns></returns>
    private (double clickX, double clickY) ConvertToGameRegionPosition(Rect bigMapInAllMapRect, double x, double y)
    {
        var (picX, picY) = MapCoordinate.GameToMain2048(x, y);
        var picRect = MapCoordinate.GameToMain2048(bigMapInAllMapRect);
        Debug.WriteLine($"({picX},{picY}) 在 {picRect} 内，计算它在窗体内的位置");
        var clickX = (picX - picRect.X) / picRect.Width * _captureRect.Width;
        var clickY = (picY - picRect.Y) / picRect.Height * _captureRect.Height;
        return (clickX, clickY);
    }

    public async Task<(double, double)> Tp(double tpX, double tpY, bool force = false)
    {
        // 重试3次
        for (var i = 0; i < 3; i++)
        {
            try
            {
                return await TpOnce(tpX, tpY, force);
            }
            catch (TpPointNotActivate e)
            {
                // 传送点未激活或不存在 按ESC回到大地图界面
                Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
                await Delay(300, ct);
                // throw; // 不抛出异常，继续重试
                Logger.LogWarning(e.Message + "  重试");
            }
            catch (Exception e)
            {
                Logger.LogError("传送失败，重试 {I} 次", i + 1);
                Logger.LogDebug(e, "传送失败，重试 {I} 次", i + 1);
            }
        }

        throw new InvalidOperationException("传送失败");
    }

    /// <summary>
    /// 移动地图到指定传送点位置
    /// 可能会移动不对，所以可以重试此方法
    /// </summary>
    /// <param name="x">目标x坐标</param>
    /// <param name="y">目标y坐标</param>
    /// <param name="tolerance">允许误差，默认50.0</param>
    /// <param name="maxIterations">最大尝试次数，默认20</param>
    public async Task MoveMapTo(double x, double y, double tolerance = 50.0, int maxIterations = 20)
    {
        // 获取当前地图中心点并计算到目标传送点的初始偏移
        int mapZoomLevel = 4;
        await AdjustMapZoomLevel(mapZoomLevel);
        var bigMapCenterPoint = GetPositionFromBigMap();  // 初始中心
        var newBigMapCenterPoint = bigMapCenterPoint;
        var (xOffset, yOffset) = (x - bigMapCenterPoint.X, y - bigMapCenterPoint.Y);
        // double distance = Math.Sqrt(xOffset * xOffset + yOffset * yOffset);        
        int moveMouseX = 100 * Math.Sign(xOffset);
        int moveMouseY = 100 * Math.Sign(yOffset);
        int moveSteps = 10;
        for (int iteration = 0; iteration < maxIterations; iteration++)
        {
            // 移动鼠标
            await MouseMoveMap(moveMouseX, moveMouseY, moveSteps);

            bigMapCenterPoint = newBigMapCenterPoint; // 保存上一次移动的数据
            newBigMapCenterPoint = GetPositionFromBigMap(); // 随循环更新的地图中心
            // 本次移动的距离
            double diffMapX = Math.Abs(newBigMapCenterPoint.X - bigMapCenterPoint.X);
            double diffMapY = Math.Abs(newBigMapCenterPoint.Y - bigMapCenterPoint.Y);
            double moveDistance = Math.Sqrt(diffMapX * diffMapX + diffMapY * diffMapY);
            
            if (moveDistance > 10) // 移动距离大于10认为本次移动成功
            {
                (xOffset, yOffset) = (x - newBigMapCenterPoint.X, y - newBigMapCenterPoint.Y); // 更新目标偏移量
                double totalMoveMouseX = Math.Abs(moveMouseX * xOffset / diffMapX);
                double totalMoveMouseY = Math.Abs(moveMouseY * yOffset / diffMapY);
                double mouseDistance = Math.Sqrt(totalMoveMouseX * totalMoveMouseX + totalMoveMouseY * totalMoveMouseY);
                // 调整地图缩放
                // mapZoomLevel<5 才显示传送锚点和秘境;
                // mapZoomLevel<3 是为了避免部分锚点过于接近导致选错锚点；
                // 风龙废墟无法避免，但是目前没有风龙废墟的脚本吧。:)
                // https://github.com/babalae/better-genshin-impact/issues/318
                if (mouseDistance < tolerance && mapZoomLevel < 3)
                {
                    Debug.WriteLine($"在 {iteration} 迭代后，已经接近目标点，不再进一步调整。");
                    break;
                }
                else if (mouseDistance > 1000 && mapZoomLevel < 6)
                {   // 缩小地图
                    await AdjustMapZoomLevel(false);
                    totalMoveMouseX *= (mapZoomLevel) / (mapZoomLevel + 1);
                    totalMoveMouseY *= (mapZoomLevel) / (mapZoomLevel + 1);
                    mouseDistance *= (mapZoomLevel) / (mapZoomLevel + 1);
                    mapZoomLevel++;
                }
                else if (mouseDistance < 200 && mapZoomLevel > 1)
                {   // 放大地图
                    await AdjustMapZoomLevel(true);
                    totalMoveMouseX *= (mapZoomLevel) / (mapZoomLevel - 1);
                    totalMoveMouseY *= (mapZoomLevel) / (mapZoomLevel - 1);
                    mouseDistance *= (mapZoomLevel) / (mapZoomLevel - 1);
                    mapZoomLevel--;
                }
                
                
                // 单次移动最大距离为 100，
                moveMouseX = (int)Math.Min(totalMoveMouseX, 100 * totalMoveMouseX / mouseDistance) * Math.Sign(xOffset);
                moveMouseY = (int)Math.Min(totalMoveMouseY, 100 * totalMoveMouseY / mouseDistance) * Math.Sign(yOffset);
                double moveMouseLength = Math.Sqrt(moveMouseX * moveMouseX + moveMouseY * moveMouseY);
                moveSteps = Math.Max((int)moveMouseLength / 10, 1);
            }
            else
            {
                Logger.LogDebug("鼠标无法移动地图，请检查！");
            }
        }
    }


    /// <summary>
    /// 调整地图缩放级别以加速移动
    /// </summary>
    /// <param name="zoomIn">是否放大地图</param>
    /// <param name="zoomLevel">缩放等级：1-6，整数，随着数字变大地图越小，细节越少。</param>
    public async Task AdjustMapZoomLevel(bool zoomIn)
    {
        if (zoomIn)
        {
            GameCaptureRegion.GameRegionClick((rect, scale) => (50 * scale, 430 * scale));
        }
        else
        {
            GameCaptureRegion.GameRegionClick((rect, scale) => (50 * scale, 650 * scale));
        }
        await Delay(50, ct);
    }
    public async Task AdjustMapZoomLevel(int zoomLevel)
    {
        for (int i = 0; i < 5; i++)
        {
            await AdjustMapZoomLevel(true);
        }
        await Delay(200, ct);
        for (int i = 0; i < zoomLevel-1; i++)
        {
            await AdjustMapZoomLevel(false);
        }
    }

    public async Task MouseMoveMap(int pixelDeltaX, int pixelDeltaY, int steps = 10)
    {
        int stepIntervalMilliseconds = 50;
        int stepX = (int)pixelDeltaX / steps;
        int stepY = (int)pixelDeltaY / steps;

        // 随机起点以避免地图移动无效
        GameCaptureRegion.GameRegionMove((rect, _) =>
            (rect.Width / 2d + Random.Shared.Next(-rect.Width / 6, rect.Width / 6),
             rect.Height / 2d + Random.Shared.Next(-rect.Height / 6, rect.Height / 6)));

        Simulation.SendInput.Mouse.LeftButtonDown();
        for (var i = 0; i < steps; i++)
        {
            Simulation.SendInput.Mouse.MoveMouseBy(stepX, stepY).Sleep(stepIntervalMilliseconds);
        }
        Simulation.SendInput.Mouse.LeftButtonUp();
        await Delay(200, ct);
    }


    public Point2f GetPositionFromBigMap()
    {
        return GetBigMapCenterPoint();
    }

    public Point2f? GetPositionFromBigMapNullable()
    {
        try
        {
            return GetBigMapCenterPoint();
        }
        catch
        {
            return null;
        }
    }

    public Rect GetBigMapRect()
    {
        var rect = new Rect();
        NewRetry.Do(() =>
        {
            // 判断是否在地图界面
            using var ra = CaptureToRectArea();
            using var mapScaleButtonRa = ra.Find(QuickTeleportAssets.Instance.MapScaleButtonRo);
            if (mapScaleButtonRa.IsExist())
            {
                rect = BigMap.Instance.GetBigMapRectByFeatureMatch(ra.SrcGreyMat);
                if (rect == Rect.Empty)
                {
                    // 滚轮调整后再次识别
                    Simulation.SendInput.Mouse.VerticalScroll(2);
                    Sleep(500);
                    throw new RetryException("识别大地图位置失败");
                }
            }
            else
            {
                throw new RetryException("当前不在地图界面");
            }
        }, TimeSpan.FromMilliseconds(500), 5);

        if (rect == Rect.Empty)
        {
            throw new InvalidOperationException("多次重试后，识别大地图位置失败");
        }

        Debug.WriteLine("识别大地图在全地图位置矩形：" + rect);
        const int s = BigMap.ScaleTo2048; // 相对1024做4倍缩放
        return MapCoordinate.Main2048ToGame(new Rect(rect.X * s, rect.Y * s, rect.Width * s, rect.Height * s));
    }

    public Point2f GetBigMapCenterPoint()
    {
        // 判断是否在地图界面
        using var ra = CaptureToRectArea();
        using var mapScaleButtonRa = ra.Find(QuickTeleportAssets.Instance.MapScaleButtonRo);
        if (mapScaleButtonRa.IsExist())
        {
            var p = BigMap.Instance.GetBigMapPositionByFeatureMatch(ra.SrcGreyMat);
            if (p.IsEmpty())
            {
                throw new InvalidOperationException("识别大地图位置失败");
            }

            Debug.WriteLine("识别大地图在全地图位置：" + p);
            return MapCoordinate.Main2048ToGame(new Point2f(BigMap.ScaleTo2048 * p.X, BigMap.ScaleTo2048 * p.Y));
        }
        else
        {
            throw new InvalidOperationException("当前不在地图界面");
        }
    }

    /// <summary>
    /// 获取最近的传送点位置
    /// </summary>
    /// <param name="x"></param>
    /// <param name="y"></param>
    /// <returns></returns>
    public (double x, double y) GetRecentlyTpPoint(double x, double y)
    {
        double recentX = 0;
        double recentY = 0;
        var minDistance = double.MaxValue;
        foreach (var tpPosition in MapLazyAssets.Instance.TpPositions)
        {
            var distance = Math.Sqrt(Math.Pow(tpPosition.X - x, 2) + Math.Pow(tpPosition.Y - y, 2));
            if (distance < minDistance)
            {
                minDistance = distance;
                recentX = tpPosition.X;
                recentY = tpPosition.Y;
            }
        }

        return (recentX, recentY);
    }

    public async Task<bool> SwitchRecentlyCountryMap(double x, double y)
    {
        // 可能是地下地图，切换到地上地图
        using var ra2 = CaptureToRectArea();
        if (Bv.BigMapIsUnderground(ra2))
        {
            ra2.Find(_assets.MapUndergroundToGroundButtonRo).Click();
            await Delay(200, ct);
        }

        // 识别当前位置
        var minDistance = double.MaxValue;
        var bigMapCenterPointNullable = GetPositionFromBigMapNullable();

        if (bigMapCenterPointNullable != null)
        {
            var bigMapCenterPoint = bigMapCenterPointNullable.Value;
            Logger.LogDebug("识别当前大地图位置：{Pos}", bigMapCenterPoint);
            minDistance = Math.Sqrt(Math.Pow(bigMapCenterPoint.X - x, 2) + Math.Pow(bigMapCenterPoint.Y - y, 2));
            if (minDistance < 50)
            {
                // 点位很近的情况下不切换
                return false;
            }
        }

        var minCountry = "当前位置";
        foreach (var (country, position) in MapLazyAssets.Instance.CountryPositions)
        {
            var distance = Math.Sqrt(Math.Pow(position[0] - x, 2) + Math.Pow(position[1] - y, 2));
            if (distance < minDistance)
            {
                minDistance = distance;
                minCountry = country;
            }
        }

        Logger.LogDebug("离目标传送点最近的区域是：{Country}", minCountry);
        if (minCountry != "当前位置")
        {
            GameCaptureRegion.GameRegionClick((rect, scale) => (rect.Width - 160 * scale, rect.Height - 60 * scale));
            await Delay(300, ct);
            var ra = CaptureToRectArea();
            var list = ra.FindMulti(new RecognitionObject
            {
                RecognitionType = RecognitionTypes.Ocr,
                RegionOfInterest = new Rect(ra.Width / 2, 0, ra.Width / 2, ra.Height)
            });
            list.FirstOrDefault(r => r.Text.Length == minCountry.Length && !r.Text.Contains("委托") && r.Text.Contains(minCountry))?.Click();
            Logger.LogInformation("切换到区域：{Country}", minCountry);
            await Delay(500, ct);
            return true;
        }

        return false;
    }

    public async Task Tp(string name)
    {
        // 通过大地图传送到指定传送点
    }

    public async Task TpByF1(string name)
    {
        // 传送到指定传送点
    }

    public async Task ClickTpPoint(ImageRegion imageRegion)
    {
        // 1.判断是否在地图界面
        if (Bv.IsInBigMapUi(imageRegion))
        {
            // 2. 判断是否已经点出传送按钮
            var hasTeleportButton = CheckTeleportButton(imageRegion);
            if (!hasTeleportButton)
            {
                // 3. 没点出传送按钮，且不存在外部地图关闭按钮
                // 说明只有两种可能，a. 点出来的是未激活传送点或者标点 b. 选择传送点选项列表
                var mapCloseRa1 = imageRegion.Find(_assets.MapCloseButtonRo);
                if (!mapCloseRa1.IsEmpty())
                {
                    throw new TpPointNotActivate("传送点未激活或不存在");
                }
                else
                {
                    // 3. 循环判断选项列表是否有传送点(未激活点位也在里面)
                    var hasMapChooseIcon = CheckMapChooseIcon(imageRegion);
                    if (hasMapChooseIcon)
                    {
                        var time = TaskContext.Instance().Config.QuickTeleportConfig.WaitTeleportPanelDelay;
                        time = time < 300 ? 300 : time;
                        await Delay(time, ct);
                        if (!CheckTeleportButton(CaptureToRectArea()))
                        {
                            // 没传送确认图标说明点开的是未激活传送锚点
                            throw new TpPointNotActivate("传送点未激活或不存在");
                        }
                    }
                    else
                    {
                        // 没有传送点说明不是传送点
                        throw new TpPointNotActivate("传送点未激活或不存在");
                    }
                }
            }
        }
    }

    private bool CheckTeleportButton(ImageRegion imageRegion)
    {
        var hasTeleportButton = false;
        imageRegion.Find(_assets.TeleportButtonRo, ra =>
        {
            ra.Click();
            hasTeleportButton = true;
        });
        return hasTeleportButton;
    }

    /// <summary>
    /// 全匹配一遍并进行文字识别
    /// 60ms ~200ms
    /// </summary>
    /// <param name="imageRegion"></param>
    /// <returns></returns>
    private bool CheckMapChooseIcon(ImageRegion imageRegion)
    {
        var hasMapChooseIcon = false;

        // 全匹配一遍
        var rResultList = MatchTemplateHelper.MatchMultiPicForOnePic(imageRegion.SrcGreyMat[_assets.MapChooseIconRoi], _assets.MapChooseIconGreyMatList);
        // 按高度排序
        if (rResultList.Count > 0)
        {
            rResultList = [.. rResultList.OrderBy(x => x.Y)];
            // 点击最高的
            foreach (var iconRect in rResultList)
            {
                // 200宽度的文字区域
                using var ra = imageRegion.DeriveCrop(_assets.MapChooseIconRoi.X + iconRect.X + iconRect.Width, _assets.MapChooseIconRoi.Y + iconRect.Y - 8, 200, iconRect.Height + 16);
                using var textRegion = ra.Find(new RecognitionObject
                {
                    // RecognitionType = RecognitionTypes.Ocr,
                    RecognitionType = RecognitionTypes.ColorRangeAndOcr,
                    LowerColor = new Scalar(249, 249, 249), // 只取白色文字
                    UpperColor = new Scalar(255, 255, 255),
                });
                if (string.IsNullOrEmpty(textRegion.Text) || textRegion.Text.Length == 1)
                {
                    continue;
                }

                Logger.LogInformation("传送：点击 {Option}", textRegion.Text.Replace(">", ""));
                var time = TaskContext.Instance().Config.QuickTeleportConfig.TeleportListClickDelay;
                time = time < 500 ? 500 : time;
                Thread.Sleep(time);
                ra.Click();
                hasMapChooseIcon = true;
                break;
            }
        }

        return hasMapChooseIcon;
    }
}
