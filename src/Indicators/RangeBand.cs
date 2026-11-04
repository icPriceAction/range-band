// =========================================================================================
// Tool Name:    Range Band
// Class:        RangeBand
// Category:     Drawing Tool
// Version:      1.3.1
// Platform:     NinjaTrader 8.0+
// Author:       Istvan Cebrian
// Brand:        icPriceAction
// Website:      https://icpriceaction.com
// Repository:   https://github.com/icPriceAction/range-band
// YouTube:      https://youtube.com/@icPriceAction
// Support:      https://buymeacoffee.com/icpriceaction
//
// License:      Free for personal trading use. Commercial resale, redistribution,
//               or repackaging is strictly prohibited. See LICENSE in official repository.
//
// Description:  Draws a trading range between resistance and support with a 50% equilibrium
//               line, optional 25% / 75% quarter lines and a range size badge in points and ticks.
//
// Changelog:
//   v1.3.1 - Initial public release. Cleaned and optimized by icPriceAction.
//          - Direct2D brushes cached per render target (no per-frame brush allocations)
//          - Stats badge text and TextLayout cached; rebuilt only when the range or font changes
//          - Mouse-move work skipped while the band is not being edited or moved
//          - Settings regrouped into "1. General" / "2. Visuals" with descriptions on every parameter
//          - Icon WPF brushes frozen; full resource cleanup on termination
//   v1.3.0 - New grey default colors (#8E8E8E lines, #C6C6C6 fill), default line width 2
//   v1.2.0 - Custom menu/toolbar icon
//          - Locked bands can no longer be edited or moved by clicking them
//          - Minimum-width protection only on creation (zoomed-out bands no longer jump)
//   v1.0.0 - Tick-snapped prices, instrument price formatting for the range readout
//          - Auto-sized badge that flips below the band when clipped at the panel top
//          - Delta-based handle resizing (no jump to cursor)
//          - New options: Extend Right, 25% / 75% quarter lines; band included in autoscale
//          - Slot-based right-margin movement, minimum width protection
// =========================================================================================

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;
#endregion

namespace NinjaTrader.NinjaScript.DrawingTools.icPA
{
	/// <summary>
	/// Trading Range Band with Resistance, Support, 50% midline, optional quarter lines,
	/// slot-based right-margin movement, tick-snapped prices and a range stats badge.
	/// </summary>
	public class RangeBand : DrawingTool
	{
		private enum DragMode
		{
			None,
			TopLeft,
			TopRight,
			BottomLeft,
			BottomRight,
			TopEdge,
			BottomEdge,
			LeftEdge,
			RightEdge,
			MovingRange
		}

		private const int AnchorRadius = 5;
		private const float HandleHitTolerance = 12f;
		private const float LineHitTolerance = 10f;
		private const float MinCreationWidthPx = 20f;
		private const float CreationWidthPushPx = 50f;

		// Fixed UI colors (badge and selection handles)
		private static readonly Color4 PillBgColor       = new Color4(0.15f, 0.15f, 0.17f, 0.88f);
		private static readonly Color4 PillFgColor       = new Color4(0.95f, 0.95f, 0.95f, 1f);
		private static readonly Color4 PillBorderColor   = new Color4(0.3f, 0.3f, 0.35f, 0.8f);
		private static readonly Color4 CornerFillColor   = new Color4(1f, 1f, 1f, 1f);
		private static readonly Color4 CornerBorderColor = new Color4(0.2f, 0.2f, 0.2f, 1f);
		private static readonly Color4 EdgeFillColor     = new Color4(0.95f, 0.95f, 0.95f, 1f);
		private static readonly Color4 EdgeBorderColor   = new Color4(0.1f, 0.5f, 0.9f, 1f);

		private ChartAnchor anchorTopLeft;
		private ChartAnchor anchorBottomRight;

		// Drag tracking
		private double dragStartMousePrice;
		private double dragStartMouseSlot;
		private double dragStartTopPrice;
		private double dragStartBottomPrice;
		private double dragStartLeftSlot;
		private double dragStartRightSlot;
		private double dragOffsetX;
		private DragMode currentDragMode = DragMode.None;

		// Cached device-independent resources
		private SharpDX.DirectWrite.TextFormat badgeFormat;
		private float badgeFormatSize = -1f;
		private StrokeStyle dashStyle;
		private StrokeStyle dotStyle;

		// Cached badge text / layout (device-independent, rebuilt only when the range, instrument or font changes)
		private string badgeText;
		private double badgeRangePts = double.NaN;
		private MasterInstrument badgeMaster;
		private SharpDX.DirectWrite.TextLayout badgeLayout;
		private float badgeW;
		private float badgeH;

		// Cached device-dependent brushes (tied to the current RenderTarget)
		private SharpDX.Direct2D1.RenderTarget cachedRenderTarget;
		private SharpDX.Direct2D1.SolidColorBrush dxFill, dxRes, dxSup, dxMid, dxQuarter;
		private SharpDX.Direct2D1.SolidColorBrush dxPillBg, dxPillFg, dxPillBorder;
		private SharpDX.Direct2D1.SolidColorBrush dxCornerFill, dxCornerBorder, dxEdgeFill, dxEdgeBorder;
		private Color4 cFill, cRes, cSup, cMid, cQuarter;
		private Color4 cPillBg, cPillFg, cPillBorder, cCornerFill, cCornerBorder, cEdgeFill, cEdgeBorder;

		#region Icon
		public override object Icon
		{
			get
			{
				// A new instance per call: the same element cannot live in both the menu and the toolbar.
				var canvas = new System.Windows.Controls.Canvas { Width = 16, Height = 16, SnapsToDevicePixels = true };

				var fillBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x55, 0xC6, 0xC6, 0xC6));
				fillBrush.Freeze();

				var fill = new System.Windows.Shapes.Rectangle
				{
					Width = 14,
					Height = 9,
					Fill = fillBrush
				};
				System.Windows.Controls.Canvas.SetLeft(fill, 1);
				System.Windows.Controls.Canvas.SetTop(fill, 3.5);
				canvas.Children.Add(fill);

				System.Windows.Media.Color grey = System.Windows.Media.Color.FromRgb(0x8E, 0x8E, 0x8E);
				canvas.Children.Add(IconLine(3.5, grey, false));   // resistance
				canvas.Children.Add(IconLine(8.0, grey, true));    // 50% midline
				canvas.Children.Add(IconLine(12.5, grey, false));  // support
				return canvas;
			}
		}

		private static System.Windows.Shapes.Line IconLine(double y, System.Windows.Media.Color color, bool dashed)
		{
			var stroke = new System.Windows.Media.SolidColorBrush(color);
			stroke.Freeze();

			var line = new System.Windows.Shapes.Line
			{
				X1 = 1,
				X2 = 15,
				Y1 = y,
				Y2 = y,
				Stroke = stroke,
				StrokeThickness = dashed ? 1 : 1.5
			};
			if (dashed)
				line.StrokeDashArray = new System.Windows.Media.DoubleCollection { 2, 1.5 };
			return line;
		}
		#endregion

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Name = "Range Band";
				Description = "Draws a trading range between resistance and support with a 50% equilibrium line, optional 25% / 75% quarter lines and a range size readout in points and ticks. Works on any chart and bar type; no Tick Replay required.\r\n"
					+ "Developed by Istvan Cebrian | https://icpriceaction.com\r\n"
					+ "Support this free tool: https://buymeacoffee.com/icpriceaction";

				anchorTopLeft = new ChartAnchor { IsEditing = true, DrawingTool = this, DisplayName = "Top Left (Resistance)" };
				anchorBottomRight = new ChartAnchor { IsEditing = false, DrawingTool = this, DisplayName = "Bottom Right (Support)" };

				MidlineColor = MakeBrush(0xFF, 0x8E, 0x8E, 0x8E);
				ResistanceColor = MakeBrush(0xFF, 0x8E, 0x8E, 0x8E);
				SupportColor = MakeBrush(0xFF, 0x8E, 0x8E, 0x8E);
				FillColor = MakeBrush(0xFF, 0xC6, 0xC6, 0xC6);

				LineWidth = 2.0f;
				FillOpacity = 10;
				ShowMidline = true;
				ShowQuarterLines = false;
				ShowRangeStats = true;
				ExtendRight = false;
				BadgeFontSize = 14f;
			}
			else if (State == State.Terminated)
			{
				DisposeDeviceResources();
				DisposeCachedResources();
			}
		}

		private static System.Windows.Media.SolidColorBrush MakeBrush(byte a, byte r, byte g, byte b)
		{
			var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(a, r, g, b));
			brush.Freeze();
			return brush;
		}

		private void DisposeCachedResources()
		{
			if (badgeLayout != null) { badgeLayout.Dispose(); badgeLayout = null; }
			if (badgeFormat != null) { badgeFormat.Dispose(); badgeFormat = null; }
			if (dashStyle != null) { dashStyle.Dispose(); dashStyle = null; }
			if (dotStyle != null) { dotStyle.Dispose(); dotStyle = null; }
			badgeFormatSize = -1f;
			badgeText = null;
			badgeRangePts = double.NaN;
			badgeMaster = null;
		}

		private static void DisposeBrush(ref SharpDX.Direct2D1.SolidColorBrush brush)
		{
			if (brush != null)
			{
				if (!brush.IsDisposed)
					brush.Dispose();
				brush = null;
			}
		}

		private void DisposeDeviceResources()
		{
			DisposeBrush(ref dxFill);
			DisposeBrush(ref dxRes);
			DisposeBrush(ref dxSup);
			DisposeBrush(ref dxMid);
			DisposeBrush(ref dxQuarter);
			DisposeBrush(ref dxPillBg);
			DisposeBrush(ref dxPillFg);
			DisposeBrush(ref dxPillBorder);
			DisposeBrush(ref dxCornerFill);
			DisposeBrush(ref dxCornerBorder);
			DisposeBrush(ref dxEdgeFill);
			DisposeBrush(ref dxEdgeBorder);
			cachedRenderTarget = null;
		}

		#region Anchors
		[Display(Order = 1)]
		public ChartAnchor AnchorTopLeft { get { return anchorTopLeft; } set { anchorTopLeft = value; } }

		[Display(Order = 2)]
		public ChartAnchor AnchorBottomRight { get { return anchorBottomRight; } set { anchorBottomRight = value; } }

		public override IEnumerable<ChartAnchor> Anchors
		{
			get { return new[] { AnchorTopLeft, AnchorBottomRight }; }
		}

		public override bool SupportsAlerts { get { return false; } }
		#endregion

		#region Properties
		// ---------- 1. General ----------
		[Display(Name = "Show 50% Midline", Description = "Draws the dashed 50% equilibrium line between resistance and support.", GroupName = "1. General", Order = 1)]
		public bool ShowMidline { get; set; }

		[Display(Name = "Show 25% / 75% Lines", Description = "Draws dotted quarter lines at 25% and 75% of the range.", GroupName = "1. General", Order = 2)]
		public bool ShowQuarterLines { get; set; }

		[Display(Name = "Show Range Stats Badge", Description = "Shows the range size in points and ticks above the band (below it when clipped at the top of the panel).", GroupName = "1. General", Order = 3)]
		public bool ShowRangeStats { get; set; }

		[Display(Name = "Extend Right", Description = "Extends the band lines and fill to the right edge of the chart panel.", GroupName = "1. General", Order = 4)]
		public bool ExtendRight { get; set; }

		// ---------- 2. Visuals ----------
		[XmlIgnore]
		[Display(Name = "Resistance Line Color", Description = "Color of the top (resistance) line.", GroupName = "2. Visuals", Order = 1)]
		public System.Windows.Media.Brush ResistanceColor { get; set; }

		[Browsable(false)]
		public string ResistanceColorSerializable
		{
			get { return Serialize.BrushToString(ResistanceColor); }
			set { ResistanceColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Support Line Color", Description = "Color of the bottom (support) line.", GroupName = "2. Visuals", Order = 2)]
		public System.Windows.Media.Brush SupportColor { get; set; }

		[Browsable(false)]
		public string SupportColorSerializable
		{
			get { return Serialize.BrushToString(SupportColor); }
			set { SupportColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "50% Midline Color", Description = "Color of the 50% midline. The 25% / 75% quarter lines use this color at lower opacity.", GroupName = "2. Visuals", Order = 3)]
		public System.Windows.Media.Brush MidlineColor { get; set; }

		[Browsable(false)]
		public string MidlineColorSerializable
		{
			get { return Serialize.BrushToString(MidlineColor); }
			set { MidlineColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Interior Fill Color", Description = "Color of the shaded area between resistance and support.", GroupName = "2. Visuals", Order = 4)]
		public System.Windows.Media.Brush FillColor { get; set; }

		[Browsable(false)]
		public string FillColorSerializable
		{
			get { return Serialize.BrushToString(FillColor); }
			set { FillColor = Serialize.StringToBrush(value); }
		}

		[Range(0, 100)]
		[Display(Name = "Fill Opacity (%)", Description = "Opacity of the interior fill. 0 turns the fill off.", GroupName = "2. Visuals", Order = 5)]
		public int FillOpacity { get; set; }

		[Range(1f, 10f)]
		[Display(Name = "Line Width", Description = "Width of the resistance and support lines in pixels.", GroupName = "2. Visuals", Order = 6)]
		public float LineWidth { get; set; }

		[Range(8f, 24f)]
		[Display(Name = "Stats Badge Font Size", Description = "Font size of the range stats badge.", GroupName = "2. Visuals", Order = 7)]
		public float BadgeFontSize { get; set; }
		#endregion

		#region Helpers
		private static double TickSizeOf(ChartControl chartControl)
		{
			return (chartControl != null && chartControl.Instrument != null && chartControl.Instrument.MasterInstrument != null)
				? chartControl.Instrument.MasterInstrument.TickSize : 0.25;
		}

		private static double Snap(ChartControl chartControl, double price)
		{
			if (chartControl != null && chartControl.Instrument != null && chartControl.Instrument.MasterInstrument != null)
				return chartControl.Instrument.MasterInstrument.RoundToTickSize(price);
			return price;
		}

		private float GetLineRight(ChartPanel chartPanel, float right)
		{
			return ExtendRight ? Math.Max(right, (float)(chartPanel.X + chartPanel.W)) : right;
		}
		#endregion

		#region Geometry Normalization & Bounds
		private void NormalizeAnchors(ChartControl chartControl, ChartPanel chartPanel, ChartScale chartScale, bool enforceMinWidth)
		{
			double tick = TickSizeOf(chartControl);
			double res = Snap(chartControl, Math.Max(AnchorTopLeft.Price, AnchorBottomRight.Price));
			double sup = Snap(chartControl, Math.Min(AnchorTopLeft.Price, AnchorBottomRight.Price));

			// Minimum height protection (price based, zoom independent)
			if (Math.Abs(res - sup) < (tick * 2))
				sup = Snap(chartControl, res - (tick * 8));

			AnchorTopLeft.Price = res;
			AnchorBottomRight.Price = sup;

			if (chartControl == null || chartPanel == null || chartScale == null)
				return;

			// Minimum visual width - creation only, so zooming out never alters an existing band
			if (enforceMinWidth)
			{
				System.Windows.Point p1 = AnchorTopLeft.GetPoint(chartControl, chartPanel, chartScale);
				System.Windows.Point p2 = AnchorBottomRight.GetPoint(chartControl, chartPanel, chartScale);

				if (Math.Abs(p2.X - p1.X) < MinCreationWidthPx)
				{
					double rightX = Math.Max(p1.X, p2.X) + CreationWidthPushPx;
					AnchorBottomRight.UpdateXFromPoint(new System.Windows.Point(rightX, p2.Y), chartControl, chartScale);
				}
			}

			// Swap times so AnchorTopLeft is always the left anchor
			System.Windows.Point pt1 = AnchorTopLeft.GetPoint(chartControl, chartPanel, chartScale);
			System.Windows.Point pt2 = AnchorBottomRight.GetPoint(chartControl, chartPanel, chartScale);

			if (pt1.X > pt2.X)
			{
				DateTime tempTime = AnchorTopLeft.Time;
				double tempSlot = AnchorTopLeft.SlotIndex;
				AnchorTopLeft.Time = AnchorBottomRight.Time;
				AnchorTopLeft.SlotIndex = AnchorBottomRight.SlotIndex;
				AnchorBottomRight.Time = tempTime;
				AnchorBottomRight.SlotIndex = tempSlot;
			}
		}

		private void GetRangeBounds(ChartControl chartControl, ChartPanel chartPanel, ChartScale chartScale,
			out float left, out float right, out float top, out float bottom, out float midY)
		{
			System.Windows.Point p1 = AnchorTopLeft.GetPoint(chartControl, chartPanel, chartScale);
			System.Windows.Point p2 = AnchorBottomRight.GetPoint(chartControl, chartPanel, chartScale);

			left = (float)Math.Min(p1.X, p2.X);
			right = (float)Math.Max(p1.X, p2.X);
			top = (float)Math.Min(p1.Y, p2.Y);
			bottom = (float)Math.Max(p1.Y, p2.Y);
			midY = top + ((bottom - top) * 0.5f);
		}

		public override void OnCalculateMinMax()
		{
			MinValue = double.MaxValue;
			MaxValue = double.MinValue;

			if (!IsVisible)
				return;

			foreach (ChartAnchor anchor in Anchors)
			{
				MinValue = Math.Min(MinValue, anchor.Price);
				MaxValue = Math.Max(MaxValue, anchor.Price);
			}
		}
		#endregion

		#region Interaction Logic
		public override System.Windows.Point[] GetSelectionPoints(ChartControl chartControl, ChartScale chartScale)
		{
			if (DrawingState == DrawingState.Building)
				return new System.Windows.Point[0];

			ChartPanel chartPanel = chartControl.ChartPanels[chartScale.PanelIndex];
			GetRangeBounds(chartControl, chartPanel, chartScale, out float l, out float r, out float t, out float b, out float mY);
			float mX = l + ((r - l) * 0.5f);

			return new[]
			{
				new System.Windows.Point(l, t),
				new System.Windows.Point(r, t),
				new System.Windows.Point(l, b),
				new System.Windows.Point(r, b),
				new System.Windows.Point(mX, t),
				new System.Windows.Point(mX, b),
				new System.Windows.Point(l, mY),
				new System.Windows.Point(r, mY)
			};
		}

		public override Cursor GetCursor(ChartControl chartControl, ChartPanel chartPanel, ChartScale chartScale, System.Windows.Point point)
		{
			if (DrawingState == DrawingState.Building)
				return Cursors.Pen;

			if (currentDragMode == DragMode.MovingRange)
				return Cursors.SizeAll;

			if (currentDragMode == DragMode.TopEdge || currentDragMode == DragMode.BottomEdge)
				return Cursors.SizeNS;

			if (currentDragMode == DragMode.LeftEdge || currentDragMode == DragMode.RightEdge)
				return Cursors.SizeWE;

			if (currentDragMode != DragMode.None)
				return Cursors.Hand;

			GetRangeBounds(chartControl, chartPanel, chartScale, out float l, out float r, out float t, out float b, out float mY);
			float mX = l + ((r - l) * 0.5f);
			Vector2 pt = new Vector2((float)point.X, (float)point.Y);

			// 1. Corners
			if ((pt - new Vector2(l, t)).Length() <= HandleHitTolerance || (pt - new Vector2(r, b)).Length() <= HandleHitTolerance ||
				(pt - new Vector2(r, t)).Length() <= HandleHitTolerance || (pt - new Vector2(l, b)).Length() <= HandleHitTolerance)
				return IsLocked ? Cursors.Arrow : Cursors.Hand;

			// 2. Top & bottom edge midpoints (resize height)
			if ((pt - new Vector2(mX, t)).Length() <= HandleHitTolerance || (pt - new Vector2(mX, b)).Length() <= HandleHitTolerance)
				return IsLocked ? Cursors.Arrow : Cursors.SizeNS;

			// 3. Left & right edge midpoints (resize width)
			if ((pt - new Vector2(l, mY)).Length() <= HandleHitTolerance || (pt - new Vector2(r, mY)).Length() <= HandleHitTolerance)
				return IsLocked ? Cursors.Arrow : Cursors.SizeWE;

			// 4. Lines & interior
			if (IsPointOverRange(chartControl, chartPanel, chartScale, point))
				return IsLocked ? Cursors.Arrow : Cursors.SizeAll;

			return null;
		}

		public override void OnMouseDown(ChartControl chartControl, ChartPanel chartPanel, ChartScale chartScale, ChartAnchor dataPoint)
		{
			if (IsLocked && DrawingState != DrawingState.Building)
				return;

			System.Windows.Point mousePt = dataPoint.GetPoint(chartControl, chartPanel, chartScale);

			switch (DrawingState)
			{
				case DrawingState.Building:
					if (AnchorTopLeft.IsEditing)
					{
						dataPoint.CopyDataValues(AnchorTopLeft);
						AnchorTopLeft.Price = Snap(chartControl, AnchorTopLeft.Price);
						AnchorTopLeft.IsEditing = false;
						AnchorBottomRight.IsEditing = true;

						// Pre-populate the second anchor so the band never starts collapsed
						AnchorBottomRight.UpdateFromPoint(new System.Windows.Point(mousePt.X + CreationWidthPushPx, mousePt.Y + 30), chartControl, chartScale);
					}
					else if (AnchorBottomRight.IsEditing)
					{
						AnchorBottomRight.UpdateFromPoint(mousePt, chartControl, chartScale);
						AnchorBottomRight.IsEditing = false;
						NormalizeAnchors(chartControl, chartPanel, chartScale, true);
						DrawingState = DrawingState.Normal;
						IsSelected = true;
					}
					break;

				case DrawingState.Normal:
					GetRangeBounds(chartControl, chartPanel, chartScale, out float l, out float r, out float t, out float b, out float mY);
					float mX = l + ((r - l) * 0.5f);
					Vector2 pt = new Vector2((float)mousePt.X, (float)mousePt.Y);

					dragStartMousePrice = dataPoint.Price;
					dragStartMouseSlot = chartControl.GetSlotIndexByX((int)mousePt.X);
					dragStartTopPrice = AnchorTopLeft.Price;
					dragStartBottomPrice = AnchorBottomRight.Price;
					dragStartLeftSlot = chartControl.GetSlotIndexByX((int)Math.Round(l));
					dragStartRightSlot = chartControl.GetSlotIndexByX((int)Math.Round(r));
					dragOffsetX = 0;

					// 1. Corners
					if ((pt - new Vector2(l, t)).Length() <= HandleHitTolerance)
						BeginEdit(DragMode.TopLeft, mousePt.X - l);
					else if ((pt - new Vector2(r, t)).Length() <= HandleHitTolerance)
						BeginEdit(DragMode.TopRight, mousePt.X - r);
					else if ((pt - new Vector2(l, b)).Length() <= HandleHitTolerance)
						BeginEdit(DragMode.BottomLeft, mousePt.X - l);
					else if ((pt - new Vector2(r, b)).Length() <= HandleHitTolerance)
						BeginEdit(DragMode.BottomRight, mousePt.X - r);
					// 2. Edge midpoints
					else if ((pt - new Vector2(mX, t)).Length() <= HandleHitTolerance)
						BeginEdit(DragMode.TopEdge, 0);
					else if ((pt - new Vector2(mX, b)).Length() <= HandleHitTolerance)
						BeginEdit(DragMode.BottomEdge, 0);
					else if ((pt - new Vector2(l, mY)).Length() <= HandleHitTolerance)
						BeginEdit(DragMode.LeftEdge, mousePt.X - l);
					else if ((pt - new Vector2(r, mY)).Length() <= HandleHitTolerance)
						BeginEdit(DragMode.RightEdge, mousePt.X - r);
					// 3. Move entire range
					else if (IsPointOverRange(chartControl, chartPanel, chartScale, mousePt))
					{
						currentDragMode = DragMode.MovingRange;
						DrawingState = DrawingState.Moving;
						IsSelected = true;
					}
					else
					{
						IsSelected = false;
						currentDragMode = DragMode.None;
					}
					break;
			}
		}

		private void BeginEdit(DragMode mode, double offsetX)
		{
			currentDragMode = mode;
			dragOffsetX = offsetX;
			DrawingState = DrawingState.Editing;
		}

		public override void OnMouseMove(ChartControl chartControl, ChartPanel chartPanel, ChartScale chartScale, ChartAnchor dataPoint)
		{
			if (IsLocked && DrawingState != DrawingState.Building)
				return;

			System.Windows.Point mousePt = dataPoint.GetPoint(chartControl, chartPanel, chartScale);

			if (DrawingState == DrawingState.Building)
			{
				if (AnchorBottomRight.IsEditing)
				{
					AnchorBottomRight.UpdateFromPoint(mousePt, chartControl, chartScale);
					AnchorBottomRight.Price = Snap(chartControl, AnchorBottomRight.Price);
				}
				return;
			}

			// Nothing to do unless a handle drag or move is in progress
			if (DrawingState != DrawingState.Editing && DrawingState != DrawingState.Moving)
				return;

			double deltaPrice = dataPoint.Price - dragStartMousePrice;
			double newTop = Snap(chartControl, dragStartTopPrice + deltaPrice);
			double newBottom = Snap(chartControl, dragStartBottomPrice + deltaPrice);
			System.Windows.Point xPt = new System.Windows.Point(mousePt.X - dragOffsetX, mousePt.Y);

			if (DrawingState == DrawingState.Editing)
			{
				switch (currentDragMode)
				{
					case DragMode.TopEdge:
						AnchorTopLeft.Price = newTop;
						break;

					case DragMode.BottomEdge:
						AnchorBottomRight.Price = newBottom;
						break;

					case DragMode.LeftEdge:
						AnchorTopLeft.UpdateXFromPoint(xPt, chartControl, chartScale);
						break;

					case DragMode.RightEdge:
						AnchorBottomRight.UpdateXFromPoint(xPt, chartControl, chartScale);
						break;

					case DragMode.TopLeft:
						AnchorTopLeft.Price = newTop;
						AnchorTopLeft.UpdateXFromPoint(xPt, chartControl, chartScale);
						break;

					case DragMode.BottomRight:
						AnchorBottomRight.Price = newBottom;
						AnchorBottomRight.UpdateXFromPoint(xPt, chartControl, chartScale);
						break;

					case DragMode.TopRight:
						AnchorTopLeft.Price = newTop;
						AnchorBottomRight.UpdateXFromPoint(xPt, chartControl, chartScale);
						break;

					case DragMode.BottomLeft:
						AnchorBottomRight.Price = newBottom;
						AnchorTopLeft.UpdateXFromPoint(xPt, chartControl, chartScale);
						break;
				}
			}
			else if (DrawingState == DrawingState.Moving && currentDragMode == DragMode.MovingRange)
			{
				// Margin-safe movement using slot deltas (works in the right margin, no compaction)
				double currentMouseSlot = chartControl.GetSlotIndexByX((int)mousePt.X);
				double deltaSlot = currentMouseSlot - dragStartMouseSlot;

				AnchorTopLeft.Price = newTop;
				AnchorBottomRight.Price = newBottom;

				AnchorTopLeft.SlotIndex = dragStartLeftSlot + deltaSlot;
				AnchorBottomRight.SlotIndex = dragStartRightSlot + deltaSlot;

				AnchorTopLeft.Time = chartControl.GetTimeBySlotIndex((int)AnchorTopLeft.SlotIndex);
				AnchorBottomRight.Time = chartControl.GetTimeBySlotIndex((int)AnchorBottomRight.SlotIndex);
			}
		}

		public override void OnMouseUp(ChartControl chartControl, ChartPanel chartPanel, ChartScale chartScale, ChartAnchor dataPoint)
		{
			if (DrawingState == DrawingState.Editing || DrawingState == DrawingState.Moving)
			{
				NormalizeAnchors(chartControl, chartPanel, chartScale, false);

				foreach (ChartAnchor anchor in Anchors)
					anchor.IsEditing = false;

				currentDragMode = DragMode.None;
				DrawingState = DrawingState.Normal;
			}
		}

		private bool IsPointOverRange(ChartControl chartControl, ChartPanel chartPanel, ChartScale chartScale, System.Windows.Point pt)
		{
			if (AnchorTopLeft == null || AnchorBottomRight == null) return false;

			GetRangeBounds(chartControl, chartPanel, chartScale, out float l, out float r, out float t, out float b, out float mY);
			float lineRight = GetLineRight(chartPanel, r);
			float tol = LineHitTolerance;

			bool withinLineX = pt.X >= (l - tol) && pt.X <= (lineRight + tol);
			bool nearTop = Math.Abs(pt.Y - t) <= tol && withinLineX;
			bool nearBottom = Math.Abs(pt.Y - b) <= tol && withinLineX;
			bool nearMid = ShowMidline && Math.Abs(pt.Y - mY) <= tol && withinLineX;
			bool nearLeft = Math.Abs(pt.X - l) <= tol && pt.Y >= (t - tol) && pt.Y <= (b + tol);
			bool nearRight = !ExtendRight && Math.Abs(pt.X - r) <= tol && pt.Y >= (t - tol) && pt.Y <= (b + tol);

			if (nearTop || nearBottom || nearMid || nearLeft || nearRight)
				return true;

			// When selected, clicking anywhere inside moves the band
			if (IsSelected && pt.X >= l && pt.X <= lineRight && pt.Y >= t && pt.Y <= b)
				return true;

			return false;
		}
		#endregion

		#region Direct2D Rendering
		private void EnsureResources()
		{
			if (dashStyle == null)
				dashStyle = new StrokeStyle(NinjaTrader.Core.Globals.D2DFactory,
					new StrokeStyleProperties { DashStyle = SharpDX.Direct2D1.DashStyle.Dash, DashCap = CapStyle.Flat });

			if (dotStyle == null)
				dotStyle = new StrokeStyle(NinjaTrader.Core.Globals.D2DFactory,
					new StrokeStyleProperties { DashStyle = SharpDX.Direct2D1.DashStyle.Dot, DashCap = CapStyle.Flat });

			if (badgeFormat == null || Math.Abs(badgeFormatSize - BadgeFontSize) > 0.01f)
			{
				if (badgeFormat != null) badgeFormat.Dispose();
				badgeFormat = new SharpDX.DirectWrite.TextFormat(
					NinjaTrader.Core.Globals.DirectWriteFactory,
					"Arial",
					SharpDX.DirectWrite.FontWeight.SemiBold,
					SharpDX.DirectWrite.FontStyle.Normal,
					BadgeFontSize);
				badgeFormat.TextAlignment = SharpDX.DirectWrite.TextAlignment.Center;
				badgeFormat.ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center;
				badgeFormat.WordWrapping = SharpDX.DirectWrite.WordWrapping.NoWrap;
				badgeFormatSize = BadgeFontSize;

				// The layout depends on the format, so rebuild it on the next badge draw
				if (badgeLayout != null) { badgeLayout.Dispose(); badgeLayout = null; }
			}
		}

		/// <summary>
		/// Returns a cached device brush for the current render target, re-creating it only when
		/// the render target or the requested color changes.
		/// </summary>
		private static SharpDX.Direct2D1.SolidColorBrush GetBrush(SharpDX.Direct2D1.RenderTarget rt,
			ref SharpDX.Direct2D1.SolidColorBrush brush, ref Color4 cachedColor, Color4 color)
		{
			if (brush == null || brush.IsDisposed || !cachedColor.Equals(color))
			{
				if (brush != null && !brush.IsDisposed)
					brush.Dispose();
				brush = new SharpDX.Direct2D1.SolidColorBrush(rt, color);
				cachedColor = color;
			}
			return brush;
		}

		private void EnsureDeviceResources(SharpDX.Direct2D1.RenderTarget rt)
		{
			// A new render target invalidates every device-dependent brush
			if (!ReferenceEquals(rt, cachedRenderTarget))
			{
				DisposeDeviceResources();
				cachedRenderTarget = rt;
			}
		}

		private void EnsureBadgeLayout(MasterInstrument master, double rangePts, double rangeTicks)
		{
			bool textChanged = badgeText == null
				|| !ReferenceEquals(master, badgeMaster)
				|| Math.Abs(rangePts - badgeRangePts) > 1e-10;

			if (textChanged)
			{
				string ptsText = master != null ? master.FormatPrice(rangePts) : rangePts.ToString("F2");
				badgeText = string.Format("Range: {0} pts ({1:F0} ticks)", ptsText, rangeTicks);
				badgeRangePts = rangePts;
				badgeMaster = master;

				if (badgeLayout != null) { badgeLayout.Dispose(); badgeLayout = null; }
			}

			if (badgeLayout == null)
			{
				badgeLayout = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, badgeText, badgeFormat, 2000f, 200f);

				float padX = BadgeFontSize * 0.8f;
				float padY = BadgeFontSize * 0.3f;
				badgeW = badgeLayout.Metrics.Width + (padX * 2f);
				badgeH = badgeLayout.Metrics.Height + (padY * 2f);

				// Size the layout box to the pill so centered alignment places the text exactly inside it
				badgeLayout.MaxWidth = badgeW;
				badgeLayout.MaxHeight = badgeH;
			}
		}

		public override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			if (AnchorTopLeft == null || AnchorBottomRight == null) return;

			SharpDX.Direct2D1.RenderTarget rt = RenderTarget;
			if (rt == null) return;

			EnsureResources();
			EnsureDeviceResources(rt);

			ChartPanel chartPanel = chartControl.ChartPanels[chartScale.PanelIndex];
			GetRangeBounds(chartControl, chartPanel, chartScale, out float left, out float right, out float top, out float bottom, out float midY);

			float width = Math.Max(right - left, 5f);
			float height = Math.Max(bottom - top, 1f);
			float lineRight = GetLineRight(chartPanel, left + width);

			// Range in points & ticks
			Instrument inst = chartControl.Instrument;
			MasterInstrument master = inst != null ? inst.MasterInstrument : null;
			double tickSize = master != null ? master.TickSize : 0.25;
			double rangePts = Math.Abs(AnchorTopLeft.Price - AnchorBottomRight.Price);
			double rangeTicks = tickSize > 0 ? Math.Round(rangePts / tickSize) : 0;

			// 1. Interior fill
			if (FillOpacity > 0)
			{
				RectangleF bandRect = new RectangleF(left, top, lineRight - left, height);
				rt.FillRectangle(bandRect, GetBrush(rt, ref dxFill, ref cFill, ToSharpDXColor(FillColor, FillOpacity / 100f)));
			}

			// 2. Resistance (top) & support (bottom)
			rt.DrawLine(new Vector2(left, top), new Vector2(lineRight, top),
				GetBrush(rt, ref dxRes, ref cRes, ToSharpDXColor(ResistanceColor, 1.0f)), LineWidth);
			rt.DrawLine(new Vector2(left, bottom), new Vector2(lineRight, bottom),
				GetBrush(rt, ref dxSup, ref cSup, ToSharpDXColor(SupportColor, 1.0f)), LineWidth);

			// 3. 50% midline and optional quarter lines
			if (ShowMidline)
			{
				rt.DrawLine(new Vector2(left, midY), new Vector2(lineRight, midY),
					GetBrush(rt, ref dxMid, ref cMid, ToSharpDXColor(MidlineColor, 0.85f)), 1.0f, dashStyle);
			}

			if (ShowQuarterLines)
			{
				SharpDX.Direct2D1.SolidColorBrush quarterBrush = GetBrush(rt, ref dxQuarter, ref cQuarter, ToSharpDXColor(MidlineColor, 0.55f));
				float q1 = top + ((bottom - top) * 0.25f);
				float q3 = top + ((bottom - top) * 0.75f);
				rt.DrawLine(new Vector2(left, q1), new Vector2(lineRight, q1), quarterBrush, 1.0f, dotStyle);
				rt.DrawLine(new Vector2(left, q3), new Vector2(lineRight, q3), quarterBrush, 1.0f, dotStyle);
			}

			// 4. Range stats badge (auto-sized; flips below the band if clipped at the panel top)
			if (ShowRangeStats)
			{
				EnsureBadgeLayout(master, rangePts, rangeTicks);

				float badgeX = left + (width / 2f) - (badgeW / 2f);
				float badgeY = top - (badgeH + 3f);

				if (badgeY < chartPanel.Y)
					badgeY = bottom + 3f;

				var pillRect = new RoundedRectangle
				{
					Rect = new RectangleF(badgeX, badgeY, badgeW, badgeH),
					RadiusX = 4f,
					RadiusY = 4f
				};

				rt.FillRoundedRectangle(pillRect, GetBrush(rt, ref dxPillBg, ref cPillBg, PillBgColor));
				rt.DrawRoundedRectangle(pillRect, GetBrush(rt, ref dxPillBorder, ref cPillBorder, PillBorderColor), 1.0f);
				rt.DrawTextLayout(new Vector2(badgeX, badgeY), badgeLayout, GetBrush(rt, ref dxPillFg, ref cPillFg, PillFgColor));
			}

			// 5. Handles when selected (4 corners + 4 edge midpoints)
			if (IsSelected)
			{
				SharpDX.Direct2D1.SolidColorBrush cornerFill = GetBrush(rt, ref dxCornerFill, ref cCornerFill, CornerFillColor);
				SharpDX.Direct2D1.SolidColorBrush cornerBorder = GetBrush(rt, ref dxCornerBorder, ref cCornerBorder, CornerBorderColor);
				SharpDX.Direct2D1.SolidColorBrush edgeFill = GetBrush(rt, ref dxEdgeFill, ref cEdgeFill, EdgeFillColor);
				SharpDX.Direct2D1.SolidColorBrush edgeBorder = GetBrush(rt, ref dxEdgeBorder, ref cEdgeBorder, EdgeBorderColor);

				float midX = left + (width * 0.5f);

				DrawCircleHandle(rt, new Vector2(left, top), cornerFill, cornerBorder);
				DrawCircleHandle(rt, new Vector2(right, top), cornerFill, cornerBorder);
				DrawCircleHandle(rt, new Vector2(left, bottom), cornerFill, cornerBorder);
				DrawCircleHandle(rt, new Vector2(right, bottom), cornerFill, cornerBorder);

				DrawCircleHandle(rt, new Vector2(midX, top), edgeFill, edgeBorder);
				DrawCircleHandle(rt, new Vector2(midX, bottom), edgeFill, edgeBorder);
				DrawCircleHandle(rt, new Vector2(left, midY), edgeFill, edgeBorder);
				DrawCircleHandle(rt, new Vector2(right, midY), edgeFill, edgeBorder);
			}
		}

		private void DrawCircleHandle(SharpDX.Direct2D1.RenderTarget rt, Vector2 center,
			SharpDX.Direct2D1.Brush fill, SharpDX.Direct2D1.Brush border)
		{
			var ellipse = new SharpDX.Direct2D1.Ellipse(center, AnchorRadius, AnchorRadius);
			rt.FillEllipse(ellipse, fill);
			rt.DrawEllipse(ellipse, border, 1.5f);
		}

		private Color4 ToSharpDXColor(System.Windows.Media.Brush wpfBrush, float alpha)
		{
			if (wpfBrush is System.Windows.Media.SolidColorBrush scb)
				return new Color4(scb.Color.R / 255f, scb.Color.G / 255f, scb.Color.B / 255f, alpha);
			return new Color4(0.5f, 0.5f, 0.5f, alpha);
		}
		#endregion
	}
}