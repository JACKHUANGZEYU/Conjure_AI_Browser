using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.Mathematics;
using MarkdownBlock = Markdig.Syntax.Block;

namespace ConjureBrowser.App.Services;

/// <summary>
/// Renders markdown text as WPF UI elements for display in the AI conversation.
/// Supports code blocks, tables, headers, lists, bold/italic, inline code, and LaTeX math.
/// </summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    // Colors
    private static readonly SolidColorBrush TextColor = new(Color.FromRgb(0xE8, 0xEA, 0xED));
    private static readonly SolidColorBrush CodeBackground = new(Color.FromRgb(0x1E, 0x1E, 0x1E));
    private static readonly SolidColorBrush CodeBorder = new(Color.FromRgb(0x3C, 0x40, 0x43));
    private static readonly SolidColorBrush InlineCodeBg = new(Color.FromRgb(0x3C, 0x40, 0x43));
    private static readonly SolidColorBrush AccentColor = new(Color.FromRgb(0x8A, 0xB4, 0xF8));
    private static readonly SolidColorBrush HeaderColor = new(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush MathColor = new(Color.FromRgb(0xFF, 0xD7, 0x00)); // Gold for math

    /// <summary>
    /// Renders markdown text to a list of WPF UIElements.
    /// </summary>
    public static List<UIElement> Render(string markdown)
    {
        var elements = new List<UIElement>();
        
        if (string.IsNullOrWhiteSpace(markdown))
            return elements;

        try
        {
            var document = Markdown.Parse(markdown, Pipeline);

            foreach (var block in document)
            {
                var element = RenderBlock(block);
                if (element != null)
                    elements.Add(element);
            }
        }
        catch
        {
            // Fallback: return plain text if parsing fails
            elements.Add(CreateTextBlock(markdown));
        }

        return elements;
    }

    private static UIElement? RenderBlock(MarkdownBlock block)
    {
        return block switch
        {
            MathBlock math => RenderMathBlock(math),  // Must be before CodeBlock (MathBlock extends CodeBlock)
            FencedCodeBlock codeBlock => RenderCodeBlock(codeBlock),
            CodeBlock codeBlock => RenderCodeBlock(codeBlock),
            HeadingBlock heading => RenderHeading(heading),
            ParagraphBlock paragraph => RenderParagraph(paragraph),
            ListBlock list => RenderList(list),
            ThematicBreakBlock => RenderHorizontalRule(),
            Markdig.Extensions.Tables.Table table => RenderTable(table),
            QuoteBlock quote => RenderQuote(quote),
            _ => null
        };
    }

    private static UIElement RenderCodeBlock(LeafBlock codeBlock)
    {
        var code = codeBlock.Lines.ToString().TrimEnd();
        var language = (codeBlock as FencedCodeBlock)?.Info ?? "";

        var container = new Border
        {
            Background = CodeBackground,
            BorderBrush = CodeBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 8, 0, 8)
        };

        var outerStack = new StackPanel();

        // Header with language and copy button
        var header = new Grid 
        { 
            Margin = new Thickness(12, 8, 12, 0),
            Background = Brushes.Transparent
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var langLabel = new TextBlock
        {
            Text = string.IsNullOrEmpty(language) ? "code" : language,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA6)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(langLabel, 0);
        header.Children.Add(langLabel);

        // Copy button with safe clipboard access
        var copyButton = new Button
        {
            Content = "📋 Copy",
            FontSize = 11,
            Background = Brushes.Transparent,
            Foreground = AccentColor,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 4, 8, 4),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        
        // Safe copy - capture code value and use dispatcher
        var codeToCopy = code;
        copyButton.Click += (sender, args) =>
        {
            try
            {
                // Use Dispatcher to ensure we're on UI thread
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    Clipboard.SetDataObject(codeToCopy, true);
                });
                
                // Visual feedback
                if (sender is Button btn)
                {
                    btn.Content = "✓ Copied!";
                    var timer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromSeconds(1.5)
                    };
                    timer.Tick += (_, _) =>
                    {
                        btn.Content = "📋 Copy";
                        timer.Stop();
                    };
                    timer.Start();
                }
            }
            catch
            {
                // Silently fail if clipboard is unavailable
            }
        };
        Grid.SetColumn(copyButton, 1);
        header.Children.Add(copyButton);
        outerStack.Children.Add(header);

        // Horizontal scroll for code content
        var scrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(12, 8, 12, 12)
        };

        // Code content - NO text wrapping, allow horizontal scroll
        var codeText = new TextBlock
        {
            Text = code,
            FontFamily = new FontFamily("Consolas, Courier New"),
            FontSize = 12,
            Foreground = TextColor,
            Background = Brushes.Transparent,
            TextWrapping = TextWrapping.NoWrap  // Important: no wrap for horizontal scroll
        };

        scrollViewer.Content = codeText;
        outerStack.Children.Add(scrollViewer);

        container.Child = outerStack;
        return container;
    }

    private static UIElement RenderHeading(HeadingBlock heading)
    {
        var text = GetInlineText(heading.Inline);
        var fontSize = heading.Level switch
        {
            1 => 20.0,
            2 => 18.0,
            3 => 16.0,
            _ => 14.0
        };

        return new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            FontWeight = FontWeights.Bold,
            Foreground = HeaderColor,
            Margin = new Thickness(0, 12, 16, 6),
            TextWrapping = TextWrapping.Wrap
        };
    }

    private static UIElement RenderMathBlock(MathBlock math)
    {
        var mathContent = math.Lines.ToString().Trim();
        return RenderLatex(mathContent, isBlock: true);
    }

    /// <summary>
    /// Renders LaTeX content using WpfMath's FormulaControl.
    /// Falls back to styled text if parsing fails.
    /// </summary>
    private static UIElement RenderLatex(string latex, bool isBlock)
    {
        try
        {
            // Use WpfMath FormulaControl for rendering
            var formulaControl = new WpfMath.Controls.FormulaControl
            {
                Formula = latex,
                Scale = isBlock ? 18 : 14,
                Foreground = MathColor
            };

            if (isBlock)
            {
                // Wrap in horizontal ScrollViewer for wide formulas
                var scrollViewer = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = formulaControl
                };

                return new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x32)),
                    Padding = new Thickness(16, 12, 16, 12),
                    Margin = new Thickness(0, 8, 0, 8),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Child = scrollViewer
                };
            }

            return formulaControl;
        }
        catch
        {
            // Fallback: display as styled text if LaTeX parsing fails
            var fallback = new TextBlock
            {
                Text = latex,
                FontFamily = new FontFamily("Cambria Math, Consolas"),
                FontSize = isBlock ? 14 : 13,
                FontStyle = FontStyles.Italic,
                Foreground = MathColor,
                TextWrapping = TextWrapping.Wrap
            };

            if (isBlock)
            {
                return new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x32)),
                    BorderBrush = MathColor,
                    BorderThickness = new Thickness(0, 0, 0, 2),
                    Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(0, 8, 0, 8),
                    Child = fallback
                };
            }

            return fallback;
        }
    }

    private static UIElement RenderParagraph(ParagraphBlock paragraph)
    {
        var textBlock = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Foreground = TextColor,
            Margin = new Thickness(0, 4, 16, 4),
            LineHeight = 20
        };

        if (paragraph.Inline != null)
        {
            RenderInlines(textBlock.Inlines, paragraph.Inline);
        }

        return textBlock;
    }

    private static void RenderInlines(InlineCollection inlines, ContainerInline container)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    inlines.Add(new Run(literal.Content.ToString()));
                    break;

                case CodeInline code:
                    var codeRun = new Run(code.Content)
                    {
                        FontFamily = new FontFamily("Consolas"),
                        Background = InlineCodeBg,
                        Foreground = AccentColor
                    };
                    inlines.Add(codeRun);
                    break;

                case MathInline math:
                    // Render math using WpfMath FormulaControl for proper LaTeX display
                    var mathContent = math.Content.ToString();
                    try
                    {
                        var formulaControl = new WpfMath.Controls.FormulaControl
                        {
                            Formula = mathContent,
                            Scale = 14,
                            Foreground = MathColor
                        };
                        
                        // Wrap in ScrollViewer for wide formulas
                        var scrollViewer = new ScrollViewer
                        {
                            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                            Content = formulaControl,
                            MaxWidth = 350,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        
                        inlines.Add(new InlineUIContainer(scrollViewer));
                    }
                    catch
                    {
                        // Fallback to styled text if WpfMath parsing fails
                        var mathRun = new Run(mathContent)
                        {
                            FontFamily = new FontFamily("Cambria Math, Times New Roman"),
                            FontStyle = FontStyles.Italic,
                            Foreground = MathColor
                        };
                        inlines.Add(mathRun);
                    }
                    break;

                case EmphasisInline emphasis:
                    var emphasisText = GetInlineText(emphasis);
                    var run = new Run(emphasisText);
                    if (emphasis.DelimiterCount == 2)
                        run.FontWeight = FontWeights.Bold;
                    else
                        run.FontStyle = FontStyles.Italic;
                    inlines.Add(run);
                    break;

                case LinkInline link:
                    var linkText = GetInlineText(link);
                    var hyperlink = new Run(linkText) { Foreground = AccentColor };
                    inlines.Add(hyperlink);
                    break;

                case LineBreakInline:
                    inlines.Add(new LineBreak());
                    break;

                default:
                    // Try to get text content
                    if (inline is ContainerInline ci)
                        RenderInlines(inlines, ci);
                    break;
            }
        }
    }

    private static UIElement RenderList(ListBlock list)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 4, 16, 4) };
        var index = 1;

        foreach (var item in list)
        {
            if (item is ListItemBlock listItem)
            {
                // Use Grid for proper alignment: bullet on left, content on right
                var itemGrid = new Grid { Margin = new Thickness(16, 2, 0, 2) };
                itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                
                var bullet = new TextBlock
                {
                    Text = list.IsOrdered ? $"{index++}. " : "• ",
                    Foreground = TextColor,
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Top
                };
                Grid.SetColumn(bullet, 0);
                itemGrid.Children.Add(bullet);

                // Content panel to hold all sub-blocks
                var contentPanel = new StackPanel();

                foreach (var subBlock in listItem)
                {
                    if (subBlock is ParagraphBlock para && para.Inline != null)
                    {
                        var textBlock = new TextBlock
                        {
                            TextWrapping = TextWrapping.Wrap,
                            FontSize = 13,
                            Foreground = TextColor,
                            Margin = new Thickness(0, 0, 0, 4)
                        };
                        RenderInlines(textBlock.Inlines, para.Inline);
                        contentPanel.Children.Add(textBlock);
                    }
                    else
                    {
                        // Handle non-paragraph blocks (nested lists, code blocks, etc.)
                        var rendered = RenderBlock(subBlock);
                        if (rendered != null)
                            contentPanel.Children.Add(rendered);
                    }
                }

                Grid.SetColumn(contentPanel, 1);
                itemGrid.Children.Add(contentPanel);
                stack.Children.Add(itemGrid);
            }
        }

        return stack;
    }

    private static UIElement RenderTable(Markdig.Extensions.Tables.Table table)
    {
        var grid = new Grid();

        // Count columns
        var columnCount = 0;
        foreach (var row in table)
        {
            if (row is Markdig.Extensions.Tables.TableRow tableRow)
            {
                columnCount = Math.Max(columnCount, tableRow.Count);
            }
        }

        for (int i = 0; i < columnCount; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        var rowIndex = 0;
        foreach (var row in table)
        {
            if (row is Markdig.Extensions.Tables.TableRow tableRow)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var colIndex = 0;
                foreach (var cell in tableRow)
                {
                    if (cell is Markdig.Extensions.Tables.TableCell tableCell)
                    {
                        var cellText = "";
                        foreach (var block in tableCell)
                        {
                            if (block is ParagraphBlock para)
                                cellText = GetInlineText(para.Inline);
                        }

                        var cellBorder = new Border
                        {
                            BorderBrush = CodeBorder,
                            BorderThickness = new Thickness(1),
                            Padding = new Thickness(10, 6, 10, 6),
                            Background = rowIndex == 0 ? CodeBackground : Brushes.Transparent
                        };

                        var cellContent = new TextBlock
                        {
                            Text = cellText,
                            FontSize = 12,
                            Foreground = TextColor,
                            FontWeight = rowIndex == 0 ? FontWeights.Bold : FontWeights.Normal,
                            TextWrapping = TextWrapping.NoWrap  // No wrap in table cells
                        };

                        cellBorder.Child = cellContent;
                        Grid.SetRow(cellBorder, rowIndex);
                        Grid.SetColumn(cellBorder, colIndex);
                        grid.Children.Add(cellBorder);
                    }
                    colIndex++;
                }
                rowIndex++;
            }
        }

        // Wrap in horizontal ScrollViewer for wide tables
        var scrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 8, 0, 8),
            Content = grid
        };

        return scrollViewer;
    }

    private static UIElement RenderQuote(QuoteBlock quote)
    {
        var border = new Border
        {
            BorderBrush = AccentColor,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 4, 16, 4),
            Margin = new Thickness(0, 4, 0, 4)
        };

        var stack = new StackPanel();
        foreach (var block in quote)
        {
            var element = RenderBlock(block);
            if (element != null)
                stack.Children.Add(element);
        }

        border.Child = stack;
        return border;
    }

    private static UIElement RenderHorizontalRule()
    {
        return new Border
        {
            Height = 1,
            Background = CodeBorder,
            Margin = new Thickness(0, 12, 0, 12)
        };
    }

    private static string GetInlineText(ContainerInline? container)
    {
        if (container == null) return "";
        
        var text = "";
        foreach (var inline in container)
        {
            text += inline switch
            {
                LiteralInline literal => literal.Content.ToString(),
                CodeInline code => code.Content,
                MathInline math => math.Content.ToString(),
                EmphasisInline emphasis => GetInlineText(emphasis),
                LinkInline link => GetInlineText(link),
                _ => ""
            };
        }
        return text;
    }

    private static TextBlock CreateTextBlock(string text)
    {
        return new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Foreground = TextColor,
            LineHeight = 20
        };
    }
}
