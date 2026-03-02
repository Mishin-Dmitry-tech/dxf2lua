using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DxfToLua
{
    public partial class MainWindow : Window
    {
        private StringBuilder reportBuilder = new StringBuilder();
        private StringBuilder luaBuilder = new StringBuilder();
        private Random random = new Random();

        // Храним текущий путь к файлу для обновления
        private string currentFilePath = null;

        private readonly Color[] contourColors = new Color[]
        {
            Colors.Red, Colors.Blue, Colors.Green, Colors.Orange, Colors.Purple,
            Colors.Brown, Colors.Cyan, Colors.Magenta, Colors.DarkGreen, Colors.DarkBlue,
            Colors.Crimson, Colors.Teal, Colors.Indigo, Colors.Tomato, Colors.SteelBlue,
            Colors.Goldenrod, Colors.SeaGreen, Colors.Coral, Colors.SlateBlue, Colors.Chocolate
        };

        public MainWindow()
        {
            InitializeComponent();
            ClearOutput();

            // Подписываемся на событие изменения размеров окна
            this.SizeChanged += MainWindow_SizeChanged;
        }

        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // При изменении размеров окна, Viewbox автоматически масштабирует содержимое
        }

        private void btnSelectFile_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = "DXF files (*.dxf)|*.dxf|All files (*.*)|*.*",
                Title = "Выберите DXF файл"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                currentFilePath = openFileDialog.FileName;
                txtFileName.Text = System.IO.Path.GetFileName(currentFilePath);
                btnRefresh.IsEnabled = true;
                ProcessDxfFile(currentFilePath);
            }
        }

        private void btnRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(currentFilePath) && File.Exists(currentFilePath))
            {
                // Показываем индикацию обновления
                Mouse.OverrideCursor = Cursors.Wait;
                try
                {
                    ProcessDxfFile(currentFilePath);
                }
                finally
                {
                    Mouse.OverrideCursor = null;
                }
            }
            else
            {
                MessageBox.Show("Файл больше не существует или был перемещен. Выберите файл заново.",
                               "Ошибка обновления",
                               MessageBoxButton.OK,
                               MessageBoxImage.Warning);
                btnRefresh.IsEnabled = false;
                currentFilePath = null;
                txtFileName.Text = "Файл не выбран";
            }
        }

        private void ClearOutput()
        {
            reportBuilder.Clear();
            luaBuilder.Clear();
            txtReport.Text = "";
            txtLuaCode.Text = "";
            txtStats.Text = "";
            canvasVisualization.Children.Clear();
        }

        private void ProcessDxfFile(string filePath)
        {
            try
            {
                ClearOutput();

                var parser = new DxfParser();
                parser.Parse(filePath);

                var contours = FindClosedContours(parser.Lines, parser.Arcs);

                GenerateReport(parser, contours);
                GenerateLuaCode(contours);
                VisualizeContours(contours);
                UpdateStats(parser, contours);
            }
            catch (FileNotFoundException)
            {
                MessageBox.Show($"Файл '{System.IO.Path.GetFileName(filePath)}' не найден.",
                               "Ошибка",
                               MessageBoxButton.OK,
                               MessageBoxImage.Error);
                btnRefresh.IsEnabled = false;
                currentFilePath = null;
                txtFileName.Text = "Файл не выбран";
            }
            catch (IOException ex) when (ex.Message.Contains("занят") ||
                                         ex.Message.Contains("used") ||
                                         ex.Message.Contains("locked"))
            {
                MessageBox.Show($"Файл '{System.IO.Path.GetFileName(filePath)}' открыт в другой программе.\n\n" +
                               "Закройте файл в другой программе и нажмите Обновить.",
                               "Файл заблокирован",
                               MessageBoxButton.OK,
                               MessageBoxImage.Warning);
            }
            catch (UnauthorizedAccessException)
            {
                MessageBox.Show($"Нет прав доступа к файлу '{System.IO.Path.GetFileName(filePath)}'.\n\n" +
                               "Проверьте права доступа к файлу.",
                               "Ошибка доступа",
                               MessageBoxButton.OK,
                               MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при обработке файла: {ex.Message}\n\n{ex.StackTrace}",
                               "Ошибка",
                               MessageBoxButton.OK,
                               MessageBoxImage.Error);
            }
        }

        private List<List<EntityWithDirection>> FindClosedContours(List<Line> lines, List<Arc> arcs)
        {
            var allEntities = new List<Entity>();
            allEntities.AddRange(lines);
            allEntities.AddRange(arcs);

            var usedEntities = new HashSet<Entity>();
            var contours = new List<List<EntityWithDirection>>();

            foreach (var startEntity in allEntities)
            {
                if (usedEntities.Contains(startEntity))
                    continue;

                var contour = new List<EntityWithDirection>();

                var currentEntity = startEntity;
                bool forward = true;
                bool closed = false;
                bool valid = true;

                while (!closed && valid)
                {
                    if (usedEntities.Contains(currentEntity))
                    {
                        valid = false;
                        break;
                    }

                    contour.Add(new EntityWithDirection
                    {
                        Entity = currentEntity,
                        Forward = forward,
                        StartPoint = forward ? currentEntity.StartPoint : currentEntity.EndPoint,
                        EndPoint = forward ? currentEntity.EndPoint : currentEntity.StartPoint
                    });

                    usedEntities.Add(currentEntity);

                    Point2D currentEndPoint = forward ? currentEntity.EndPoint : currentEntity.StartPoint;

                    var nextCandidates = allEntities
                        .Where(e => !usedEntities.Contains(e))
                        .Select(e => new
                        {
                            Entity = e,
                            StartToCurrentEnd = GetDistance(e.StartPoint, currentEndPoint),
                            EndToCurrentEnd = GetDistance(e.EndPoint, currentEndPoint)
                        })
                        .Select(x => new
                        {
                            x.Entity,
                            Distance = Math.Min(x.StartToCurrentEnd, x.EndToCurrentEnd),
                            UseForward = x.StartToCurrentEnd <= x.EndToCurrentEnd,
                            MatchPoint = x.StartToCurrentEnd <= x.EndToCurrentEnd ? x.Entity.StartPoint : x.Entity.EndPoint
                        })
                        .Where(x => x.Distance < 1e-3)
                        .OrderBy(x => x.Distance)
                        .ToList();

                    if (nextCandidates.Any())
                    {
                        var bestMatch = nextCandidates.First();
                        currentEntity = bestMatch.Entity;
                        forward = bestMatch.UseForward;
                    }
                    else
                    {
                        var firstElement = contour[0];
                        Point2D firstPoint = firstElement.StartPoint;

                        double distanceToFirst = GetDistance(currentEndPoint, firstPoint);

                        if (contour.Count > 1 && distanceToFirst < 1e-3)
                        {
                            closed = true;
                        }
                        else
                        {
                            valid = false;
                        }
                    }
                }

                if (closed && valid && contour.Count > 0)
                {
                    contours.Add(contour);
                }
                else
                {
                    foreach (var item in contour)
                    {
                        usedEntities.Remove(item.Entity);
                    }
                }
            }

            return contours;
        }

        private double GetDistance(Point2D p1, Point2D p2)
        {
            double dx = p1.X - p2.X;
            double dy = p1.Y - p2.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private void GenerateReport(DxfParser parser, List<List<EntityWithDirection>> contours)
        {
            reportBuilder.AppendLine("=== ОТЧЕТ ОБРАБОТКИ DXF ФАЙЛА ===\n");
            reportBuilder.AppendLine($"Всего найдено линий: {parser.Lines.Count}");
            reportBuilder.AppendLine($"Всего найдено арок: {parser.Arcs.Count}");
            reportBuilder.AppendLine($"Всего сущностей: {parser.Lines.Count + parser.Arcs.Count}");
            reportBuilder.AppendLine($"\nНайдено замкнутых контуров: {contours.Count}");

            int totalLinesInContours = 0;
            int totalArcsInContours = 0;

            for (int i = 0; i < contours.Count; i++)
            {
                var contour = contours[i];
                int linesCount = contour.Count(e => e.Entity.Type == EntityType.Line);
                int arcsCount = contour.Count(e => e.Entity.Type == EntityType.Arc);

                totalLinesInContours += linesCount;
                totalArcsInContours += arcsCount;

                Color color = GetContourColor(i);
                reportBuilder.AppendLine($"\nКонтур #{i + 1}:");
                reportBuilder.AppendLine($"  Количество элементов: {contour.Count}");
                reportBuilder.AppendLine($"  Линий: {linesCount}");
                reportBuilder.AppendLine($"  Арок: {arcsCount}");
                reportBuilder.AppendLine($"  Цвет: RGB({color.R}, {color.G}, {color.B})");
            }

            reportBuilder.AppendLine($"\n=== ИТОГИ ===");
            reportBuilder.AppendLine($"Всего линий в контурах: {totalLinesInContours}");
            reportBuilder.AppendLine($"Всего арок в контурах: {totalArcsInContours}");
            reportBuilder.AppendLine($"Неиспользованных линий: {parser.Lines.Count - totalLinesInContours}");
            reportBuilder.AppendLine($"Неиспользованных арок: {parser.Arcs.Count - totalArcsInContours}");

            txtReport.Text = reportBuilder.ToString();
        }

        private void GenerateLuaCode(List<List<EntityWithDirection>> contours)
        {
            luaBuilder.Clear();

            if (contours.Count == 0)
            {
                luaBuilder.AppendLine("-- Замкнутых контуров не найдено");
            }
            else
            {
                for (int i = 0; i < contours.Count; i++)
                {
                    var contour = contours[i];
                    Color color = GetContourColor(i);

                    luaBuilder.AppendLine($"-- Контур #{i + 1} [Color=#{color.R:X2}{color.G:X2}{color.B:X2}]");

                    luaBuilder.Append($"shape{i + 1} = CreateCompositeCurve2D( {{");

                    for (int j = 0; j < contour.Count; j++)
                    {
                        var element = contour[j];

                        if (element.Entity.Type == EntityType.Line)
                        {
                            var line = (Line)element.Entity;

                            Point2D start = element.Forward ? line.Start : line.End;
                            Point2D end = element.Forward ? line.End : line.Start;

                            string startX = start.X.ToString("F4", CultureInfo.InvariantCulture);
                            string startY = start.Y.ToString("F4", CultureInfo.InvariantCulture);
                            string endX = end.X.ToString("F4", CultureInfo.InvariantCulture);
                            string endY = end.Y.ToString("F4", CultureInfo.InvariantCulture);

                            luaBuilder.AppendLine();
                            luaBuilder.Append($"    CreateLineSegment2D( Point2D({startX}, {startY}), Point2D({endX}, {endY}) )");
                        }
                        else
                        {
                            var arc = (Arc)element.Entity;

                            Point2D start, mid, end;

                            if (element.Forward)
                            {
                                start = arc.Start;
                                mid = arc.Mid;
                                end = arc.End;
                            }
                            else
                            {
                                start = arc.End;
                                end = arc.Start;
                                mid = arc.Mid;
                            }

                            string startX = start.X.ToString("F4", CultureInfo.InvariantCulture);
                            string startY = start.Y.ToString("F4", CultureInfo.InvariantCulture);
                            string midX = mid.X.ToString("F4", CultureInfo.InvariantCulture);
                            string midY = mid.Y.ToString("F4", CultureInfo.InvariantCulture);
                            string endX = end.X.ToString("F4", CultureInfo.InvariantCulture);
                            string endY = end.Y.ToString("F4", CultureInfo.InvariantCulture);

                            luaBuilder.AppendLine();
                            luaBuilder.Append($"    CreateArc2DByThreePoints( Point2D({startX}, {startY}), Point2D({midX}, {midY}), Point2D({endX}, {endY}) )");
                        }

                        if (j < contour.Count - 1)
                            luaBuilder.Append(",");
                    }

                    luaBuilder.AppendLine();
                    luaBuilder.AppendLine("} )");
                    luaBuilder.AppendLine();
                }
            }

            txtLuaCode.Text = luaBuilder.ToString();
        }

        private void VisualizeContours(List<List<EntityWithDirection>> contours)
        {
            canvasVisualization.Children.Clear();

            if (contours.Count == 0)
            {
                TextBlock textBlock = new TextBlock
                {
                    Text = "Нет замкнутых контуров для отображения",
                    FontSize = 16,
                    Foreground = new SolidColorBrush(Colors.Gray)
                };

                Canvas.SetLeft(textBlock, 300);
                Canvas.SetTop(textBlock, 250);
                canvasVisualization.Children.Add(textBlock);

                if (txtScaleInfo != null)
                    txtScaleInfo.Text = "Масштаб: 1:1 (нет данных)";
                return;
            }

            // Находим границы всех контуров для масштабирования
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            foreach (var contour in contours)
            {
                foreach (var element in contour)
                {
                    if (element.Entity.Type == EntityType.Line)
                    {
                        var line = (Line)element.Entity;
                        minX = Math.Min(minX, line.Start.X);
                        minX = Math.Min(minX, line.End.X);
                        maxX = Math.Max(maxX, line.Start.X);
                        maxX = Math.Max(maxX, line.End.X);

                        minY = Math.Min(minY, line.Start.Y);
                        minY = Math.Min(minY, line.End.Y);
                        maxY = Math.Max(maxY, line.Start.Y);
                        maxY = Math.Max(maxY, line.End.Y);
                    }
                    else
                    {
                        var arc = (Arc)element.Entity;
                        minX = Math.Min(minX, arc.Start.X);
                        minX = Math.Min(minX, arc.Mid.X);
                        minX = Math.Min(minX, arc.End.X);
                        maxX = Math.Max(maxX, arc.Start.X);
                        maxX = Math.Max(maxX, arc.Mid.X);
                        maxX = Math.Max(maxX, arc.End.X);

                        minY = Math.Min(minY, arc.Start.Y);
                        minY = Math.Min(minY, arc.Mid.Y);
                        minY = Math.Min(minY, arc.End.Y);
                        maxY = Math.Max(maxY, arc.Start.Y);
                        maxY = Math.Max(maxY, arc.Mid.Y);
                        maxY = Math.Max(maxY, arc.End.Y);
                    }
                }
            }

            // Добавляем отступы
            double padding = 20;
            minX -= padding;
            minY -= padding;
            maxX += padding;
            maxY += padding;

            double dataWidth = maxX - minX;
            double dataHeight = maxY - minY;

            double canvasWidth = canvasVisualization.Width;
            double canvasHeight = canvasVisualization.Height;

            double scaleX = (canvasWidth * 0.9) / dataWidth;
            double scaleY = (canvasHeight * 0.9) / dataHeight;
            double scale = Math.Min(scaleX, scaleY);

            double worldScale = scale;

            if (txtScaleInfo != null)
                txtScaleInfo.Text = $"Масштаб: 1:{1 / worldScale:F2} (Canvas: {canvasWidth}×{canvasHeight}, Данные: {dataWidth:F1}×{dataHeight:F1})";

            Func<double, double, System.Windows.Point> transform = (x, y) =>
            {
                double centerX = (minX + maxX) / 2;
                double centerY = (minY + maxY) / 2;

                double shiftedX = x - centerX;
                double shiftedY = y - centerY;

                double scaledX = shiftedX * scale;
                double scaledY = shiftedY * scale;

                double canvasX = scaledX + canvasWidth / 2;
                double canvasY = canvasHeight / 2 - scaledY;

                return new System.Windows.Point(canvasX, canvasY);
            };

            for (int i = 0; i < contours.Count; i++)
            {
                var contour = contours[i];
                Color color = GetContourColor(i);
                var brush = new SolidColorBrush(color);

                for (int j = 0; j < contour.Count; j++)
                {
                    var element = contour[j];

                    if (element.Entity.Type == EntityType.Line)
                    {
                        var line = (Line)element.Entity;
                        Point2D start = element.Forward ? line.Start : line.End;
                        Point2D end = element.Forward ? line.End : line.Start;

                        var startPoint = transform(start.X, start.Y);
                        var endPoint = transform(end.X, end.Y);

                        var lineShape = new System.Windows.Shapes.Line
                        {
                            X1 = startPoint.X,
                            Y1 = startPoint.Y,
                            X2 = endPoint.X,
                            Y2 = endPoint.Y,
                            Stroke = brush,
                            StrokeThickness = 2,
                            StrokeEndLineCap = PenLineCap.Round,
                            StrokeStartLineCap = PenLineCap.Round
                        };

                        canvasVisualization.Children.Add(lineShape);
                    }
                    else
                    {
                        var arc = (Arc)element.Entity;

                        Point2D center = arc.Center;
                        double radius = arc.Radius;

                        double startAngle = Math.Atan2(arc.Start.Y - center.Y, arc.Start.X - center.X);
                        double endAngle = Math.Atan2(arc.End.Y - center.Y, arc.End.X - center.X);

                        if (startAngle < 0) startAngle += 2 * Math.PI;
                        if (endAngle < 0) endAngle += 2 * Math.PI;

                        double midAngle = Math.Atan2(arc.Mid.Y - center.Y, arc.Mid.X - center.X);
                        if (midAngle < 0) midAngle += 2 * Math.PI;

                        double angleDiff;
                        bool goThroughZero = false;

                        if (startAngle < endAngle)
                        {
                            angleDiff = endAngle - startAngle;
                            goThroughZero = (midAngle < startAngle || midAngle > endAngle);
                        }
                        else
                        {
                            angleDiff = (2 * Math.PI - startAngle) + endAngle;
                            goThroughZero = (midAngle > endAngle && midAngle < startAngle);
                        }

                        if (goThroughZero)
                        {
                            angleDiff = 2 * Math.PI - angleDiff;
                        }

                        double drawStartAngle = startAngle;

                        if (!element.Forward)
                        {
                            drawStartAngle = endAngle;
                            angleDiff = -angleDiff;
                        }

                        int segments = 60;
                        var points = new List<System.Windows.Point>();

                        for (int k = 0; k <= segments; k++)
                        {
                            double t = (double)k / segments;
                            double angle = drawStartAngle + t * angleDiff;

                            double normAngle = angle;
                            while (normAngle < 0) normAngle += 2 * Math.PI;
                            while (normAngle >= 2 * Math.PI) normAngle -= 2 * Math.PI;

                            double x = center.X + radius * Math.Cos(normAngle);
                            double y = center.Y + radius * Math.Sin(normAngle);

                            var point = transform(x, y);
                            points.Add(point);
                        }

                        for (int k = 0; k < points.Count - 1; k++)
                        {
                            var lineShape = new System.Windows.Shapes.Line
                            {
                                X1 = points[k].X,
                                Y1 = points[k].Y,
                                X2 = points[k + 1].X,
                                Y2 = points[k + 1].Y,
                                Stroke = brush,
                                StrokeThickness = 2,
                                StrokeEndLineCap = PenLineCap.Round,
                                StrokeStartLineCap = PenLineCap.Round
                            };

                            canvasVisualization.Children.Add(lineShape);
                        }
                    }
                }

                foreach (var element in contour)
                {
                    var point = transform(element.StartPoint.X, element.StartPoint.Y);

                    var ellipse = new System.Windows.Shapes.Ellipse
                    {
                        Width = 6,
                        Height = 6,
                        Fill = new SolidColorBrush(Colors.Black),
                        Stroke = brush,
                        StrokeThickness = 1
                    };

                    Canvas.SetLeft(ellipse, point.X - 3);
                    Canvas.SetTop(ellipse, point.Y - 3);
                    canvasVisualization.Children.Add(ellipse);
                }
            }

            // Добавляем легенду и оси координат
            AddLegendAndAxes(contours, transform);
        }

        private void AddLegendAndAxes(List<List<EntityWithDirection>> contours, Func<double, double, System.Windows.Point> transform)
        {
            // Добавляем легенду в левом верхнем углу
            double legendX = 10;
            double legendY = 10;

            for (int i = 0; i < contours.Count; i++)
            {
                Color color = GetContourColor(i);

                var rect = new System.Windows.Shapes.Rectangle
                {
                    Width = 20,
                    Height = 20,
                    Fill = new SolidColorBrush(color),
                    Stroke = new SolidColorBrush(Colors.Black),
                    StrokeThickness = 1
                };

                Canvas.SetLeft(rect, legendX);
                Canvas.SetTop(rect, legendY + i * 25);
                canvasVisualization.Children.Add(rect);

                var textBlock = new TextBlock
                {
                    Text = $"Контур #{i + 1}",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Colors.Black)
                };

                Canvas.SetLeft(textBlock, legendX + 25);
                Canvas.SetTop(textBlock, legendY + i * 25 + 2);
                canvasVisualization.Children.Add(textBlock);
            }

            // Рисуем оси координат в точке (0,0) черным цветом
            System.Windows.Point origin = transform(0, 0);

            // Длина осей (в пикселях Canvas)
            double axisLength = 50;

            // Черная кисть для осей
            var blackBrush = new SolidColorBrush(Colors.Black);

            // Ось X
            var axisX = new System.Windows.Shapes.Line
            {
                X1 = origin.X,
                Y1 = origin.Y,
                X2 = origin.X + axisLength,
                Y2 = origin.Y,
                Stroke = blackBrush,
                StrokeThickness = 2,
                StrokeEndLineCap = PenLineCap.Triangle
            };
            canvasVisualization.Children.Add(axisX);

            // Ось Y
            var axisY = new System.Windows.Shapes.Line
            {
                X1 = origin.X,
                Y1 = origin.Y,
                X2 = origin.X,
                Y2 = origin.Y - axisLength, // Минус потому что Y в Canvas направлен вниз
                Stroke = blackBrush,
                StrokeThickness = 2,
                StrokeEndLineCap = PenLineCap.Triangle
            };
            canvasVisualization.Children.Add(axisY);

            // Подписи осей черным цветом
            var textX = new TextBlock
            {
                Text = "X",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = blackBrush
            };
            Canvas.SetLeft(textX, origin.X + axisLength + 2);
            Canvas.SetTop(textX, origin.Y - 10);
            canvasVisualization.Children.Add(textX);

            var textY = new TextBlock
            {
                Text = "Y",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = blackBrush
            };
            Canvas.SetLeft(textY, origin.X + 5);
            Canvas.SetTop(textY, origin.Y - axisLength - 15);
            canvasVisualization.Children.Add(textY);

            // Добавляем маленькую точку в начале координат для наглядности
            var originDot = new System.Windows.Shapes.Ellipse
            {
                Width = 5,
                Height = 5,
                Fill = blackBrush
            };
            Canvas.SetLeft(originDot, origin.X - 2.5);
            Canvas.SetTop(originDot, origin.Y - 2.5);
            canvasVisualization.Children.Add(originDot);
        }

        private void AddLegend(List<List<EntityWithDirection>> contours)
        {
            double legendX = 10;
            double legendY = 10;

            for (int i = 0; i < contours.Count; i++)
            {
                Color color = GetContourColor(i);

                var rect = new System.Windows.Shapes.Rectangle
                {
                    Width = 20,
                    Height = 20,
                    Fill = new SolidColorBrush(color),
                    Stroke = new SolidColorBrush(Colors.Black),
                    StrokeThickness = 1
                };

                Canvas.SetLeft(rect, legendX);
                Canvas.SetTop(rect, legendY + i * 25);
                canvasVisualization.Children.Add(rect);

                var textBlock = new TextBlock
                {
                    Text = $"Контур #{i + 1}",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Colors.Black)
                };

                Canvas.SetLeft(textBlock, legendX + 25);
                Canvas.SetTop(textBlock, legendY + i * 25 + 2);
                canvasVisualization.Children.Add(textBlock);
            }

            double canvasWidth = canvasVisualization.Width;
            double canvasHeight = canvasVisualization.Height;

            var axisX = new System.Windows.Shapes.Line
            {
                X1 = 10,
                Y1 = canvasHeight - 20,
                X2 = 60,
                Y2 = canvasHeight - 20,
                Stroke = new SolidColorBrush(Colors.Gray),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 2, 2 }
            };
            canvasVisualization.Children.Add(axisX);

            var axisY = new System.Windows.Shapes.Line
            {
                X1 = 10,
                Y1 = canvasHeight - 20,
                X2 = 10,
                Y2 = canvasHeight - 70,
                Stroke = new SolidColorBrush(Colors.Gray),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 2, 2 }
            };
            canvasVisualization.Children.Add(axisY);

            var textX = new TextBlock { Text = "X", FontSize = 10, Foreground = new SolidColorBrush(Colors.Gray) };
            Canvas.SetLeft(textX, 65);
            Canvas.SetTop(textX, canvasHeight - 35);
            canvasVisualization.Children.Add(textX);

            var textY = new TextBlock { Text = "Y", FontSize = 10, Foreground = new SolidColorBrush(Colors.Gray) };
            Canvas.SetLeft(textY, 15);
            Canvas.SetTop(textY, canvasHeight - 85);
            canvasVisualization.Children.Add(textY);
        }

        private Color GetContourColor(int index)
        {
            if (index < contourColors.Length)
                return contourColors[index];
            else
                return Color.FromRgb((byte)random.Next(100, 255),
                                     (byte)random.Next(100, 255),
                                     (byte)random.Next(100, 255));
        }

        private void UpdateStats(DxfParser parser, List<List<EntityWithDirection>> contours)
        {
            int linesInContours = contours.Sum(c => c.Count(e => e.Entity.Type == EntityType.Line));
            int arcsInContours = contours.Sum(c => c.Count(e => e.Entity.Type == EntityType.Arc));

            txtStats.Text = $"Линий: {parser.Lines.Count} (в контурах: {linesInContours}) | " +
                           $"Арок: {parser.Arcs.Count} (в контурах: {arcsInContours}) | " +
                           $"Замкнутых контуров: {contours.Count}";
        }
    }

    #region Data Models

    public class EntityWithDirection
    {
        public Entity Entity { get; set; }
        public bool Forward { get; set; }
        public Point2D StartPoint { get; set; }
        public Point2D EndPoint { get; set; }
    }

    public enum EntityType
    {
        Line,
        Arc
    }

    public abstract class Entity
    {
        public EntityType Type { get; protected set; }
        public abstract Point2D StartPoint { get; }
        public abstract Point2D EndPoint { get; }
    }

    public class Point2D
    {
        public double X { get; set; }
        public double Y { get; set; }

        public Point2D(double x, double y)
        {
            X = x;
            Y = y;
        }
    }

    public class Line : Entity
    {
        public Point2D Start { get; set; }
        public Point2D End { get; set; }

        public override Point2D StartPoint => Start;
        public override Point2D EndPoint => End;

        public Line(Point2D start, Point2D end)
        {
            Type = EntityType.Line;
            Start = start;
            End = end;
        }
    }

    public class Arc : Entity
    {
        public Point2D Start { get; set; }
        public Point2D Mid { get; set; }
        public Point2D End { get; set; }
        public Point2D Center { get; set; }
        public double Radius { get; set; }
        public double StartAngle { get; set; }
        public double EndAngle { get; set; }

        public override Point2D StartPoint => Start;
        public override Point2D EndPoint => End;
        public Point2D MidPoint => Mid;

        public Arc(Point2D start, Point2D mid, Point2D end, Point2D center, double radius, double startAngle, double endAngle)
        {
            Type = EntityType.Arc;
            Start = start;
            Mid = mid;
            End = end;
            Center = center;
            Radius = radius;
            StartAngle = startAngle;
            EndAngle = endAngle;
        }
    }

    #endregion

    #region DXF Parser

    public class DxfParser
    {
        public List<Line> Lines { get; } = new List<Line>();
        public List<Arc> Arcs { get; } = new List<Arc>();

        public void Parse(string filePath)
        {
            string[] lines;

            try
            {
                using (var fileStream = new FileStream(filePath,
                                                       FileMode.Open,
                                                       FileAccess.Read,
                                                       FileShare.ReadWrite))
                using (var streamReader = new StreamReader(fileStream))
                {
                    var content = streamReader.ReadToEnd();
                    lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Ошибка чтения файла: {ex.Message}", ex);
            }

            int i = 0;
            while (i < lines.Length)
            {
                string line = lines[i].Trim();

                if (line == "LINE")
                {
                    i = ParseLine(lines, i);
                }
                else if (line == "ARC")
                {
                    i = ParseArc(lines, i);
                }
                else
                {
                    i++;
                }
            }
        }

        private int ParseLine(string[] lines, int startIndex)
        {
            int i = startIndex;
            double x1 = 0, y1 = 0, x2 = 0, y2 = 0;
            bool hasStart = false, hasEnd = false;

            i++;

            while (i < lines.Length)
            {
                string code = lines[i].Trim();
                i++;

                if (i >= lines.Length) break;

                string value = lines[i].Trim();

                switch (code)
                {
                    case "10":
                        x1 = double.Parse(value, CultureInfo.InvariantCulture);
                        break;
                    case "20":
                        y1 = double.Parse(value, CultureInfo.InvariantCulture);
                        hasStart = true;
                        break;
                    case "11":
                        x2 = double.Parse(value, CultureInfo.InvariantCulture);
                        break;
                    case "21":
                        y2 = double.Parse(value, CultureInfo.InvariantCulture);
                        hasEnd = true;
                        break;
                    case "0":
                        if (hasStart && hasEnd)
                        {
                            Lines.Add(new Line(new Point2D(x1, y1), new Point2D(x2, y2)));
                        }
                        return i;
                }

                i++;
            }

            if (hasStart && hasEnd)
            {
                Lines.Add(new Line(new Point2D(x1, y1), new Point2D(x2, y2)));
            }

            return i;
        }

        private int ParseArc(string[] lines, int startIndex)
        {
            int i = startIndex;
            double cx = 0, cy = 0, radius = 0, startAngle = 0, endAngle = 0;
            bool hasCenter = false, hasRadius = false, hasAngles = false;

            i++;

            while (i < lines.Length)
            {
                string code = lines[i].Trim();
                i++;

                if (i >= lines.Length) break;

                string value = lines[i].Trim();

                switch (code)
                {
                    case "10":
                        cx = double.Parse(value, CultureInfo.InvariantCulture);
                        break;
                    case "20":
                        cy = double.Parse(value, CultureInfo.InvariantCulture);
                        hasCenter = true;
                        break;
                    case "40":
                        radius = double.Parse(value, CultureInfo.InvariantCulture);
                        hasRadius = true;
                        break;
                    case "50":
                        startAngle = double.Parse(value, CultureInfo.InvariantCulture);
                        hasAngles = true;
                        break;
                    case "51":
                        endAngle = double.Parse(value, CultureInfo.InvariantCulture);
                        break;
                    case "0":
                        if (hasCenter && hasRadius && hasAngles)
                        {
                            var arc = CreateArcFromCenterRadiusAngles(cx, cy, radius, startAngle, endAngle);
                            if (arc != null)
                                Arcs.Add(arc);
                        }
                        return i;
                }

                i++;
            }

            if (hasCenter && hasRadius && hasAngles)
            {
                var arc = CreateArcFromCenterRadiusAngles(cx, cy, radius, startAngle, endAngle);
                if (arc != null)
                    Arcs.Add(arc);
            }

            return i;
        }

        private Arc CreateArcFromCenterRadiusAngles(double cx, double cy, double radius, double startAngle, double endAngle)
        {
            try
            {
                double startRad = startAngle * Math.PI / 180.0;
                double endRad = endAngle * Math.PI / 180.0;

                var start = new Point2D(
                    cx + radius * Math.Cos(startRad),
                    cy + radius * Math.Sin(startRad)
                );

                var end = new Point2D(
                    cx + radius * Math.Cos(endRad),
                    cy + radius * Math.Sin(endRad)
                );

                double midAngle;

                double startNorm = startAngle;
                double endNorm = endAngle;

                if (endAngle < startAngle)
                {
                    endNorm += 360;
                }

                midAngle = (startNorm + endNorm) / 2.0;
                if (midAngle >= 360)
                {
                    midAngle -= 360;
                }

                double midRad = midAngle * Math.PI / 180.0;

                var mid = new Point2D(
                    cx + radius * Math.Cos(midRad),
                    cy + radius * Math.Sin(midRad)
                );

                var center = new Point2D(cx, cy);

                return new Arc(start, mid, end, center, radius, startAngle, endAngle);
            }
            catch
            {
                return null;
            }
        }
    }

    #endregion
}