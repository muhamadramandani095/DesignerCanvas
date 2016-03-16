﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Undefined.DesignerCanvas.ObjectModel;
using Undefined.DesignerCanvas.Primitive;

namespace Undefined.DesignerCanvas
{
    /// <summary>
    /// Hosts a canvas that supports diagram designing.
    /// </summary>
    [TemplatePart(Name = "PART_Canvas", Type = typeof (Canvas))]
    public class DesignerCanvas : Control, IScrollInfo
    {
        private readonly GraphicalObjectCollection _Items = new GraphicalObjectCollection();
        private readonly GraphicalObjectCollection _SelectedItems = new GraphicalObjectCollection();

        private readonly GraphicalObjectContainerGenerator _ItemContainerGenerator = new GraphicalObjectContainerGenerator();

        #region Properties

        // ItemTemplate & ItemTemplateSelector affect both the Entity & the Connection.

        public DataTemplate EntityItemTemplate
        {
            get { return (DataTemplate)GetValue(EntityItemTemplateProperty); }
            set { SetValue(EntityItemTemplateProperty, value); }
        }

        public static readonly DependencyProperty EntityItemTemplateProperty =
            DependencyProperty.Register("EntityItemTemplate", typeof (DataTemplate),
                typeof (DesignerCanvas), new PropertyMetadata(null));

        public DataTemplateSelector ItemTemplateSelector
        {
            get { return (DataTemplateSelector)GetValue(ItemTemplateSelectorProperty); }
            set { SetValue(ItemTemplateSelectorProperty, value); }
        }

        public static readonly DependencyProperty ItemTemplateSelectorProperty =
            DependencyProperty.Register("ItemTemplateSelector", typeof (DataTemplateSelector),
                typeof (DesignerCanvas), new PropertyMetadata(null));

        #endregion

        #region Items & States

        public GraphicalObjectCollection Items => _Items;

        public GraphicalObjectCollection SelectedItems => _SelectedItems;

        public GraphicalObjectContainerGenerator ItemContainerGenerator => _ItemContainerGenerator;

        // Indicates whether containers' IsSelected property is beging changed to
        // correspond with SelectedItems collection.
        private bool isSelectedContainersSynchronizing = false;

        private void _SelectedItems_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            try
            {
                if (isSelectedContainersSynchronizing) throw new InvalidOperationException("此函数不支持递归调用。");
                isSelectedContainersSynchronizing = true;
                switch (e.Action)
                {
                    case NotifyCollectionChangedAction.Add:
                    case NotifyCollectionChangedAction.Remove:
                    case NotifyCollectionChangedAction.Replace:
                        if (e.OldItems != null)
                        {
                            foreach (var item in e.OldItems)
                            {
                                var container = _ItemContainerGenerator.ContainerFromItem((IGraphicalObject) item);
                                container?.SetValue(Selector.IsSelectedProperty, false);
                            }
                        }
                        if (e.NewItems != null)
                        {
                            foreach (var item in e.NewItems)
                            {
                                var container = _ItemContainerGenerator.ContainerFromItem((IGraphicalObject) item);
                                container?.SetValue(Selector.IsSelectedProperty, true);
                            }
                        }
                        break;
                    case NotifyCollectionChangedAction.Reset:
                        var unselectedContainers = partCanvas.Children.Cast<DependencyObject>().ToList();
                        foreach (var item in SelectedItems)
                        {
                            var container = _ItemContainerGenerator.ContainerFromItem(item);
                            if (container != null)
                            {
                                container.SetValue(Selector.IsSelectedProperty, true);
                                unselectedContainers.Remove(container);
                            }
                        }
                        foreach (var item in unselectedContainers)
                        {
                            item.SetValue(Selector.IsSelectedProperty, false);
                        }
                        break;
                }
            }
            finally
            {
                isSelectedContainersSynchronizing = false;
            }
        }

        private void _Items_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (partCanvas == null)
            {
                // The control may have not been loaded.
                return;
            }
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                case NotifyCollectionChangedAction.Remove:
                case NotifyCollectionChangedAction.Replace:
                    if (e.OldItems != null)
                    {
                        foreach (var item in e.OldItems)
                            SetContainerVisibility((IGraphicalObject) item, false);
                    }
                    if (e.NewItems != null)
                    {
                        foreach (var item in e.NewItems)
                        {
                            var obj = item as IGraphicalObject;
                            if (obj == null) continue;
                            if (_ViewPortRect.IntersectsWith(obj.Bounds))
                            {
                                SetContainerVisibility(obj, true);
                            }
                        }
                    }
                    break;
                case NotifyCollectionChangedAction.Reset:
                    _ItemContainerGenerator.RecycleAll();
                    partCanvas.Children.Clear();
                    foreach (var item in _Items.ObjectsInRegion(_ViewPortRect, ItemSelectionOptions.IncludePartialSelection))
                        _ItemContainerGenerator.CreateContainer(item);
                    break;
            }
            this.InvalidateMeasure();
        }

        #endregion

        #region UI

        private Canvas partCanvas;
        private TranslateTransform canvasTransform = new TranslateTransform();

        internal static DesignerCanvas FindDesignerCanvas(DependencyObject childContainer)
        {
            while (childContainer != null)
            {
                childContainer = VisualTreeHelper.GetParent(childContainer);
                var dc = childContainer as DesignerCanvas;
                if (dc != null) return dc;
            }
            return null;
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            partCanvas = (Canvas) GetTemplateChild("PART_Canvas");
            if (partCanvas == null)
            {
                partCanvas = new Canvas();
                this.AddVisualChild(partCanvas);
            }
            partCanvas.RenderTransform = canvasTransform;
            partCanvas.MouseDown += PartCanvas_MouseDown;
            partCanvas.MouseMove += PartCanvas_MouseMove;
            partCanvas.MouseUp += PartCanvas_MouseUp;
        }

        private Point? RubberbandStartPoint = null;

        private void PartCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.Source == partCanvas)
            {
                RubberbandStartPoint = e.GetPosition(partCanvas);
            }
        }

        private void PartCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (RubberbandStartPoint != null)
            {
                if (e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed)
                {
                    var adornerLayer = AdornerLayer.GetAdornerLayer(partCanvas);
                    if (adornerLayer != null)
                    {
                        var adorner = new RubberbandAdorner(this, RubberbandStartPoint.Value,
                            new Vector(_ViewPortRect.X, _ViewPortRect.Y), Rubberband_Callback);
                        adornerLayer.Add(adorner);
                        RubberbandStartPoint = null;
                    }
                }
            }
        }

        private void PartCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            RubberbandStartPoint = null;
            if (e.Source == partCanvas)
            {
                _SelectedItems.Clear();
            }
        }

        private void Rubberband_Callback(object o, Rect rect)
        {
            var mod = Keyboard.Modifiers;
            if ((mod & (ModifierKeys.Shift | ModifierKeys.Control)) == ModifierKeys.None)
            {
                _SelectedItems.Clear();
                _SelectedItems.AddRange(Items.ObjectsInRegion(rect));
            }
            else
            {
                var newItems = new HashSet<IGraphicalObject>(Items.ObjectsInRegion(rect));
                if ((mod & ModifierKeys.Shift) == ModifierKeys.Shift)
                {
                    // Switch
                    var intersection = _SelectedItems.Where(i => newItems.Contains(i)).ToList();
                    foreach (var item in intersection)
                    {
                        _SelectedItems.Remove(item);
                        newItems.Remove(item);
                    }
                    _SelectedItems.AddRange(newItems);
                }
                else if ((mod & ModifierKeys.Control) == ModifierKeys.Control)
                {
                    // Merge
                    foreach (var item in _SelectedItems) newItems.Remove(item);
                    _SelectedItems.AddRange(newItems);
                }
            }
        }

        protected override Size MeasureOverride(Size constraint)
        {
            const double measurementMargin = 10;
            var bounds = _Items.Bounds;
            if (bounds.IsEmpty) bounds = new Rect(0, 0, 0, 0);
            bounds.Width += measurementMargin;
            bounds.Height += measurementMargin;
            _ExtendRect = bounds;
            // Right now we do not support scrolling to the left / top of the origin.
            _ExtendRect.Width += _ExtendRect.X;
            _ExtendRect.Height += _ExtendRect.Y;
            _ExtendRect.X = _ExtendRect.Y = 0;
            if (HorizontalAlignment == HorizontalAlignment.Stretch && !double.IsInfinity(constraint.Width))
                if (_ExtendRect.Width < constraint.Width) _ExtendRect.Width = constraint.Width;
            if (VerticalAlignment == VerticalAlignment.Stretch && !double.IsInfinity(constraint.Height))
                if (_ExtendRect.Height < constraint.Height) _ExtendRect.Height = constraint.Height;
            partCanvas.Measure(_ExtendRect.Size); // Seems no use.
            _ViewPortRect.Size = constraint;
            InvalidateViewPortRect();
            ScrollOwner?.InvalidateScrollInfo();
            if (double.IsInfinity(constraint.Width) || double.IsInfinity(constraint.Height))
            {
                return _ExtendRect.Size;
            }
            return constraint;
        }

        protected override Size ArrangeOverride(Size arrangeBounds)
        {
            if (this.VisualChildrenCount > 0)
            {
                // Resize the canvas to fit the visual boundary.
                // Note the canvas may be contained in a border or other controls
                // so we should update the first visual child instead of the partCanvas.
                var uiElement = this.GetVisualChild(0) as UIElement;
                uiElement?.Arrange(_ExtendRect);
            }
            return arrangeBounds;
        }

        private Rect lastRenderedViewPortRect;

        /// <summary>
        /// Update ViewPort rectangle & its children when needed.
        /// </summary>
        private void InvalidateViewPortRect()
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (lastRenderedViewPortRect != _ViewPortRect)
                {
                    if (lastRenderedViewPortRect != _ViewPortRect)
                    {
                        // Generate / Recycle Items
                        OnViewPortChanged(lastRenderedViewPortRect, _ViewPortRect);
                        canvasTransform.X = -_ViewPortRect.Left;
                        canvasTransform.Y = -_ViewPortRect.Top;
                        ScrollOwner?.InvalidateScrollInfo();
                    }
                    lastRenderedViewPortRect = _ViewPortRect;
                }
            }, DispatcherPriority.Render);
        }

        private void OnViewPortChanged(Rect oldViewPort, Rect newViewPort)
        {
            double delta;
            const double safetyMargin = 10;

            delta = newViewPort.X - oldViewPort.X;
            if (delta > 0)
                SetContainerVisibility(new Rect(_ExtendRect.X - safetyMargin, _ExtendRect.Y - safetyMargin,
                    newViewPort.X - (_ExtendRect.X - safetyMargin), _ExtendRect.Height - safetyMargin*2), false);
            else if (delta < 0)
                SetContainerVisibility(new Rect(newViewPort.X, newViewPort.Y, -delta, newViewPort.Height), true);

            delta = newViewPort.Y - oldViewPort.Y;
            if (delta > 0)
                SetContainerVisibility(new Rect(_ExtendRect.X - safetyMargin, _ExtendRect.Y - safetyMargin,
                    _ExtendRect.Width + safetyMargin*2, newViewPort.Y - (_ExtendRect.Y - safetyMargin)), false);
            else if (delta < 0)
                SetContainerVisibility(new Rect(newViewPort.X, newViewPort.Y, newViewPort.Width, -delta), true);

            delta = newViewPort.Right - oldViewPort.Right;
            if (delta > 0)
                SetContainerVisibility(new Rect(oldViewPort.Right, newViewPort.Y, delta, newViewPort.Height), true);
            else if (delta < 0)
                SetContainerVisibility(new Rect(newViewPort.Right, double.MinValue/2,
                    double.MaxValue, double.MaxValue), false);

            delta = newViewPort.Bottom - oldViewPort.Bottom;
            if (delta > 0)
                SetContainerVisibility(new Rect(newViewPort.X, oldViewPort.Bottom, newViewPort.Width, delta), true);
            else if (delta < 0)
                SetContainerVisibility(new Rect(double.MinValue/2, newViewPort.Bottom,
                    double.MaxValue, double.MaxValue), false);
        }

        ///// <summary>
        ///// Creates or recycles the container for specified item,
        ///// depending whether the bounds of item is in ViewPort.
        ///// </summary>
        //private void SetContainerVisibility(Entity item)
        //{
        //    if (item == null) throw new ArgumentNullException(nameof(item));
        //    SetContainerVisibility(item, _ViewPortRect.IntersectsWith(item.Bounds));
        //}

        private void SetContainerVisibility(IGraphicalObject item, bool visible)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            if (visible)
            {
                if (_ItemContainerGenerator.ContainerFromItem(item) == null)
                {
                    var container = _ItemContainerGenerator.CreateContainer(item);
                    // Note these 2 statements shouldn't be swapped,
                    // so the container will not unnecessarily fire NotifyItemIsSelectedChanged().
                    container.SetValue(Selector.IsSelectedProperty, _SelectedItems.Contains(item));
                    partCanvas.Children.Add((UIElement) container);
                }
            }
            else
            {
                var container = _ItemContainerGenerator.ContainerFromItem(item);
                if (container != null)
                {
                    partCanvas.Children.Remove((UIElement) container);
                    _ItemContainerGenerator.Recycle(container);
                }
            }
        }

        /// <summary>
        /// Decides whether to render childen in certain rectangle.
        /// </summary>
        private void SetContainerVisibility(Rect rect, bool visible)
        {
            // Allow partial shown containers.
            // Hide when the container is contained in rect.
            foreach (var obj in _Items.ObjectsInRegion(rect, visible
                ? ItemSelectionOptions.IncludePartialSelection
                : ItemSelectionOptions.None))
                SetContainerVisibility(obj, visible);
        }

        #endregion

        #region Notifications from Children

        internal void NotifyItemMouseDown(DesignerCanvasEntity container)
        {
            Debug.Assert(container != null);
            //Debug.Print("NotifyItemMouseDown");
            if (container.IsSelected == false)
            {
                // Left click to selecte an object.
                if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == ModifierKeys.None)
                {
                    // Unselect other items first.
                    _SelectedItems.Clear();
                }
                _SelectedItems.Add(_ItemContainerGenerator.ItemFromContainer(container));
            }
        }

        internal void NotifyItemIsSelectedChanged(DependencyObject container)
        {
            Debug.Assert(container != null);
            // Do not update SelectedItems when SelectedItems are being updated.
            if (isSelectedContainersSynchronizing) return;
            var item = _ItemContainerGenerator.ItemFromContainer(container);
            if (item == null) return;
            if (Selector.GetIsSelected(container))
            {
                Debug.Assert(!SelectedItems.Contains(item));
                SelectedItems.Add(item);
            }
            else
            {
                var reuslt = SelectedItems.Remove(item);
                Debug.Assert(reuslt);
            }
        }

        #endregion

        public DesignerCanvas()
        {
            // Note PartCanvas property returns null here.
            _Items.CollectionChanged += _Items_CollectionChanged;
            _SelectedItems.CollectionChanged += _SelectedItems_CollectionChanged;
            _ItemContainerGenerator.ContainerPreparing += ItemContainerGenerator_ContainerPreparing;
        }

        private void ItemContainerGenerator_ContainerPreparing(object sender, ContainerPreparingEventArgs e)
        {
            if (EntityItemTemplate != null || ItemTemplateSelector != null)
            {
                e.Container.SetValue(ContentControl.ContentTemplateProperty, EntityItemTemplate);
                e.Container.SetValue(ContentControl.ContentTemplateSelectorProperty, ItemTemplateSelector);
            }
            else
            {
                e.Container.ClearValue(ContentControl.ContentTemplateProperty);
                e.Container.ClearValue(ContentControl.ContentTemplateSelectorProperty);
            }
        }

        static DesignerCanvas()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(DesignerCanvas),
                new FrameworkPropertyMetadata(typeof(DesignerCanvas)));
        }

        #region IScrollInfo

        private Rect _ExtendRect; // Boundary of virtual canvas.
        private Rect _ViewPortRect;

        private const double ScrollStepIncrement = 10;
        private const double ScrollPageStepPreservation = 10;
        private const double ScrollWheelStepIncrementRel = 1.0 / 3;

        /// <summary>
        /// Scrolls up within content by one logical unit. 
        /// </summary>
        public void LineUp()
        {
            SetVerticalOffset(VerticalOffset - ScrollStepIncrement);
        }

        /// <summary>
        /// Scrolls down within content by one logical unit. 
        /// </summary>
        public void LineDown()
        {
            SetVerticalOffset(VerticalOffset + ScrollStepIncrement);
        }

        /// <summary>
        /// Scrolls left within content by one logical unit.
        /// </summary>
        public void LineLeft()
        {
            SetHorizontalOffset(HorizontalOffset - ScrollStepIncrement);
        }

        /// <summary>
        /// Scrolls right within content by one logical unit.
        /// </summary>
        public void LineRight()
        {
            SetHorizontalOffset(HorizontalOffset + ScrollStepIncrement);
        }

        /// <summary>
        /// Scrolls up within content by one page.
        /// </summary>
        public void PageUp()
        {
            SetVerticalOffset(VerticalOffset - _ViewPortRect.Height);
        }

        /// <summary>
        /// Scrolls down within content by one page.
        /// </summary>
        public void PageDown()
        {
            SetVerticalOffset(VerticalOffset + _ViewPortRect.Height);
        }

        /// <summary>
        /// Scrolls left within content by one page.
        /// </summary>
        public void PageLeft()
        {
            SetHorizontalOffset(HorizontalOffset - _ViewPortRect.Width);
        }

        /// <summary>
        /// Scrolls right within content by one page.
        /// </summary>
        public void PageRight()
        {
            SetHorizontalOffset(HorizontalOffset + _ViewPortRect.Width);
        }

        /// <summary>
        /// Scrolls up within content after a user clicks the wheel button on a mouse.
        /// </summary>
        public void MouseWheelUp()
        {
            SetVerticalOffset(VerticalOffset - _ViewPortRect.Height * ScrollWheelStepIncrementRel);
        }

        /// <summary>
        /// Scrolls down within content after a user clicks the wheel button on a mouse.
        /// </summary>
        public void MouseWheelDown()
        {
            SetVerticalOffset(VerticalOffset + _ViewPortRect.Height * ScrollWheelStepIncrementRel);
        }

        /// <summary>
        /// Scrolls left within content after a user clicks the wheel button on a mouse.
        /// </summary>
        public void MouseWheelLeft()
        {
            SetHorizontalOffset(HorizontalOffset - _ViewPortRect.Width * ScrollWheelStepIncrementRel);
        }

        /// <summary>
        /// Scrolls right within content after a user clicks the wheel button on a mouse.
        /// </summary>
        public void MouseWheelRight()
        {
            SetHorizontalOffset(HorizontalOffset + _ViewPortRect.Width * ScrollWheelStepIncrementRel);
        }

        /// <summary>
        /// Sets the amount of horizontal offset.
        /// </summary>
        /// <param name="offset">The degree to which content is horizontally offset from the containing viewport.</param>
        public void SetHorizontalOffset(double offset)
        {
            if (offset > _ExtendRect.Width - _ViewPortRect.Width) offset = _ExtendRect.Width - _ViewPortRect.Width;
            if (offset < 0) offset = 0;
            _ViewPortRect.X = offset;
            //ScrollOwner?.InvalidateScrollInfo();
            InvalidateViewPortRect();
        }

        /// <summary>
        /// Sets the amount of vertical offset.
        /// </summary>
        /// <param name="offset">The degree to which content is vertically offset from the containing viewport.</param>
        public void SetVerticalOffset(double offset)
        {
            if (offset > _ExtendRect.Height - _ViewPortRect.Height) offset = _ExtendRect.Height - _ViewPortRect.Height;
            if (offset < 0) offset = 0;
            _ViewPortRect.Y = offset;
            //ScrollOwner?.InvalidateScrollInfo();
            InvalidateViewPortRect();
        }

        /// <summary>
        /// Forces content to scroll until the coordinate space of a <see cref="T:System.Windows.Media.Visual"/> object is visible. 
        /// </summary>
        /// <returns>
        /// A <see cref="T:System.Windows.Rect"/> that is visible.
        /// </returns>
        /// <param name="visual">A <see cref="T:System.Windows.Media.Visual"/> that becomes visible.</param><param name="rectangle">A bounding rectangle that identifies the coordinate space to make visible.</param>
        public Rect MakeVisible(Visual visual, Rect rectangle)
        {
            if (visual == null) throw new ArgumentNullException(nameof(visual));
            var ltPoint = visual.PointToScreen(new Point(0, 0));
            ltPoint = partCanvas.PointFromScreen(ltPoint);
            var fe = visual as FrameworkElement;
            if (fe != null)
            {
                // Make sure the center of the visual will be shown.
                ltPoint.Offset(fe.ActualWidth / 2, fe.ActualHeight / 2);
            }
            // Now the coordinate of visual is relative to the canvas.
            if (!_ViewPortRect.Contains(ltPoint))
            {
                SetHorizontalOffset(ltPoint.X);
                SetVerticalOffset(ltPoint.Y);
            }
            return rectangle;
        }

        /// <summary>
        /// Gets or sets a value that indicates whether scrolling on the vertical axis is possible. 
        /// </summary>
        /// <returns>
        /// true if scrolling is possible; otherwise, false. This property has no default value.
        /// </returns>
        public bool CanVerticallyScroll { get; set; }

        /// <summary>
        /// Gets or sets a value that indicates whether scrolling on the horizontal axis is possible.
        /// </summary>
        /// <returns>
        /// true if scrolling is possible; otherwise, false. This property has no default value.
        /// </returns>
        public bool CanHorizontallyScroll { get; set; }

        /// <summary>
        /// Gets the horizontal size of the extent.
        /// </summary>
        /// <returns>
        /// A <see cref="T:System.Double"/> that represents, in device independent pixels, the horizontal size of the extent. This property has no default value.
        /// </returns>
        public double ExtentWidth => _ExtendRect.Width;

        /// <summary>
        /// Gets the vertical size of the extent.
        /// </summary>
        /// <returns>
        /// A <see cref="T:System.Double"/> that represents, in device independent pixels, the vertical size of the extent.This property has no default value.
        /// </returns>
        public double ExtentHeight => _ExtendRect.Height;

        /// <summary>
        /// Gets the horizontal size of the viewport for this content.
        /// </summary>
        /// <returns>
        /// A <see cref="T:System.Double"/> that represents, in device independent pixels, the horizontal size of the viewport for this content. This property has no default value.
        /// </returns>
        public double ViewportWidth => _ViewPortRect.Width;

        /// <summary>
        /// Gets the vertical size of the viewport for this content.
        /// </summary>
        /// <returns>
        /// A <see cref="T:System.Double"/> that represents, in device independent pixels, the vertical size of the viewport for this content. This property has no default value.
        /// </returns>
        public double ViewportHeight => _ViewPortRect.Height;

        /// <summary>
        /// Gets the horizontal offset of the scrolled content.
        /// </summary>
        /// <returns>
        /// A <see cref="T:System.Double"/> that represents, in device independent pixels, the horizontal offset. This property has no default value.
        /// </returns>
        public double HorizontalOffset => _ViewPortRect.X;

        /// <summary>
        /// Gets the vertical offset of the scrolled content.
        /// </summary>
        /// <returns>
        /// A <see cref="T:System.Double"/> that represents, in device independent pixels, the vertical offset of the scrolled content. Valid values are between zero and the <see cref="P:System.Windows.Controls.Primitives.IScrollInfo.ExtentHeight"/> minus the <see cref="P:System.Windows.Controls.Primitives.IScrollInfo.ViewportHeight"/>. This property has no default value.
        /// </returns>
        public double VerticalOffset => _ViewPortRect.Y;

        /// <summary>
        /// Gets or sets a <see cref="T:System.Windows.Controls.ScrollViewer"/> element that controls scrolling behavior.
        /// </summary>
        /// <returns>
        /// A <see cref="T:System.Windows.Controls.ScrollViewer"/> element that controls scrolling behavior. This property has no default value.
        /// </returns>
        public ScrollViewer ScrollOwner { get; set; }

        #endregion

        #region Debug Support
#if DEBUG
        public int RenderedChildrenCount => partCanvas.Children.Count;
#endif

        #endregion
    }

    /// <summary>
    /// Generates <see cref="UIElement"/>s for GraphicalObjects.
    /// Note <see cref="GraphicalObjectCollection"/> has no indexer.
    /// </summary>
    public class GraphicalObjectContainerGenerator // aka. Factory
    {
        /// <summary>
        /// An attached property for item container, set to the corrspoinding source item.
        /// </summary>
        private static readonly DependencyProperty DataItemProperty =
            DependencyProperty.RegisterAttached("DataItem", typeof (object), typeof (GraphicalObjectContainerGenerator),
                new FrameworkPropertyMetadata(null));

        private readonly Dictionary<IGraphicalObject, DependencyObject> containerDict =
            new Dictionary<IGraphicalObject, DependencyObject>();

        private readonly ObjectPool<DesignerCanvasEntity> entityContainerPool =
            new ObjectPool<DesignerCanvasEntity>(() => new DesignerCanvasEntity());

        private readonly ObjectPool<DesignerCanvasConnection> connectionontainerPool =
            new ObjectPool<DesignerCanvasConnection>(() => new DesignerCanvasConnection());

        public event EventHandler<ContainerPreparingEventArgs> ContainerPreparing;

        public GraphicalObjectContainerGenerator()
        {
            MaxPooledContainers = 100;
        }

        /// <summary>
        /// Specifies the maximum number of containers that can exist in the pool.
        /// </summary>
        public int MaxPooledContainers
        {
            get { return entityContainerPool.Capacity; }
            set { entityContainerPool.Capacity = connectionontainerPool.Capacity = value; }
        }

        /// <summary>
        /// Gets a new or pooled container for a specific Entity.
        /// </summary>
        public DependencyObject CreateContainer(IGraphicalObject item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            DependencyObject doContainer;
            if (item is IEntity)
            {
                var container = entityContainerPool.Take();
                PrepareContainer(container, item);
                doContainer = container;
            }
            else if (item is IConnection)
            {
                var container = connectionontainerPool.Take();
                PrepareContainer(container, item);
                doContainer = container;
            }
            else
            {
                throw new ArgumentException(null, nameof(item));
            }
            containerDict.Add(item, doContainer);
            return doContainer;
        }

        private void PrepareContainer(DependencyObject container, IGraphicalObject item)
        {
            if (container == null) throw new ArgumentNullException(nameof(container));
            container.SetValue(DataItemProperty, item);
            container.SetValue(FrameworkElement.DataContextProperty, item);
            OnContainerPreparing(new ContainerPreparingEventArgs(container, item));
        }

        /// <summary>
        /// Declares a container no longer be used and should be pooled or discarded.
        /// </summary>
        public void Recycle(DependencyObject container)
        {
            Recycle(container, true);
        }

        /// <summary>
        /// Declares a container no longer be used and should be pooled or discarded.
        /// </summary>
        private void Recycle(DependencyObject container, bool removeContainerDictEntry)
        {
            if (container == null) throw new ArgumentNullException(nameof(container));
            var item = ItemFromContainer(container);
            if (item == null) throw new InvalidOperationException("试图回收非列表项目。");
            if (removeContainerDictEntry) containerDict.Remove(item);
            if (container is DesignerCanvasEntity)
            {
                entityContainerPool.PutBack((DesignerCanvasEntity) container);
            } else if (container is DesignerCanvasConnection)
            {
                connectionontainerPool.PutBack((DesignerCanvasConnection) container);
            }
            else
            {
                throw new ArgumentException(null, nameof(item));
            }
        }

        public void RecycleAll()
        {
            foreach (var container in containerDict.Values)
                Recycle(container, false);
            containerDict.Clear();
        }

        /// <summary>
        /// Gets the container, if generated, for a specific item.
        /// </summary>
        /// <returns></returns>
        public DependencyObject ContainerFromItem(IGraphicalObject item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            DependencyObject container;
            if (containerDict.TryGetValue(item, out container))
                return container;
            return null;
        }

        /// <summary>
        /// Gets the corresponding item, if exists, for a specific container.
        /// </summary>
        /// <returns></returns>
        public IGraphicalObject ItemFromContainer(DependencyObject container)
        {
            if (container == null) throw new ArgumentNullException(nameof(container));
            var localValue = container.ReadLocalValue(DataItemProperty);
            if (localValue == DependencyProperty.UnsetValue) localValue = null;
            return (IGraphicalObject) localValue;
        }

        protected virtual void OnContainerPreparing(ContainerPreparingEventArgs e)
        {
            ContainerPreparing?.Invoke(this, e);
        }
    }

    public class ContainerPreparingEventArgs : EventArgs
    {
        public DependencyObject Container { get; }
        public IGraphicalObject DataContext { get; }

        public ContainerPreparingEventArgs(DependencyObject container, IGraphicalObject dataContext)
        {
            Container = container;
            DataContext = dataContext;
        }
    }
}
