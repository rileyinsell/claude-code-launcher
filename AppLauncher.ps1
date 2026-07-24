# ============================================================================
#  Dev App Launcher  -  click a tile to open Claude in the project folder
#  and auto-start that app.
#
#  TO ADD / EDIT A TILE: just edit the $Apps list below. That's the only part
#  you ever need to touch.
#    Name   = text shown on the tile
#    Path   = the project folder (terminal opens here)
#    Prompt = the first thing Claude is told to do (it auto-starts the app)
# ============================================================================

$Apps = @(
    @{ Name = 'chektwo';        Path = 'C:\Dev\chektwo';              Prompt = 'Run `npm run dev` to start the app (it serves on port 5000), then tell me the local URL once it is up.' }
    @{ Name = 'hivemind';       Path = 'C:\Dev\hivemind';            Prompt = 'Run `pnpm run dev` to start the app (it brings up docker compose then the api + web apps). Tell me the local URLs once they are up.' }
    @{ Name = 'mixotrophic';    Path = 'C:\Dev\mixotrophic';         Prompt = 'Run `pnpm dev` to start the web app, then tell me the local URL once it is up.' }
    @{ Name = 'Aithena-Admin';  Path = 'C:\Dev\Aithena-Admin';       Prompt = 'Start the dev server for this project. Read CLAUDE.md / package.json to find the right command (it is a pnpm monorepo), run it, then tell me the local URL.' }
    @{ Name = 'aithena-llm';    Path = 'C:\Dev\aithena-llm';         Prompt = 'Start this Python project. Check README / requirements.txt to find how to run the backend, start it, then tell me the local URL.' }
    @{ Name = 'aquasignal';     Path = 'C:\Dev\aquasignal';          Prompt = 'Start this app. Check README.md / gsd.sh for how to run the frontend + backend, start them, then tell me the local URLs.' }
    @{ Name = 'tpo-dashboard';  Path = 'C:\Dev\tpo-dashboard';       Prompt = 'Start this project. Read CLAUDE.md / package.json / README to find the right dev command, run it, then tell me the local URL.' }
    @{ Name = 'blue-aqua-v2';   Path = 'C:\Dev\blue-aqua-v2';        Prompt = 'Start this project. Read CLAUDE.md / package.json / README to find the right dev command, run it, then tell me the local URL.' }
    @{ Name = 'muse';           Path = 'C:\Dev\muse';                Prompt = 'Open this project. Read SKILL.md to understand what it does and tell me how to run it (it is a set of Python scripts).' }
    @{ Name = 'love-in-motion'; Path = 'C:\Dev\love-in-motion-website'; Prompt = 'Serve this static site locally (e.g. `python -m http.server 8080`) and tell me the local URL.' }
)

# --- nothing below here needs editing for normal use --------------------------

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

# pick the best terminal command available (set once)
$script:HasWt = [bool](Get-Command wt.exe -ErrorAction SilentlyContinue)

function Start-AppTile {
    param($App)

    # Build the command the new terminal runs: name the tab, cd into folder, launch claude.
    $path   = ($App.Path   -replace "'", "''")
    $prompt = ($App.Prompt -replace "'", "''")
    $name   = ($App.Name   -replace "'", "''")
    $inner  = "`$Host.UI.RawUI.WindowTitle = '$name'; Set-Location -LiteralPath '$path'; claude '$prompt'"

    # Base64-encode it so quoting/special chars can never break the call.
    $enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($inner))

    if ($script:HasWt) {
        # Windows Terminal, opened with a friendly tab title in the project folder.
        Start-Process wt.exe -ArgumentList @(
            '-w', '0', 'new-tab',
            '--title', $App.Name,
            '--suppressApplicationTitle',
            '-d', $App.Path,
            'powershell.exe', '-NoExit', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $enc
        )
    } else {
        Start-Process powershell.exe -ArgumentList @(
            '-NoExit', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $enc
        )
    }
}

# ---------------------------------------------------------------------------
#  UI
# ---------------------------------------------------------------------------
[xml]$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Dev Launcher" Height="560" Width="720"
        WindowStartupLocation="CenterScreen" Background="#0F172A"
        ResizeMode="CanResizeWithGrip">
  <DockPanel>
    <Border DockPanel.Dock="Top" Background="#111C32" Padding="20,16">
      <StackPanel>
        <TextBlock Text="Dev Launcher" Foreground="#F8FAFC" FontSize="22" FontWeight="Bold"/>
        <TextBlock Text="Click an app — opens a terminal in the folder and starts Claude on it."
                   Foreground="#94A3B8" FontSize="12" Margin="0,4,0,0"/>
      </StackPanel>
    </Border>
    <ScrollViewer VerticalScrollBarVisibility="Auto" Padding="14">
      <WrapPanel x:Name="Tiles" Orientation="Horizontal"/>
    </ScrollViewer>
  </DockPanel>
</Window>
'@

$reader = New-Object System.Xml.XmlNodeReader $xaml
$window = [Windows.Markup.XamlReader]::Load($reader)
$tiles  = $window.FindName('Tiles')

# a rotating palette so tiles are easy to tell apart
$palette = @('#2563EB','#0891B2','#7C3AED','#DB2777','#059669','#D97706','#DC2626','#4F46E5','#0D9488','#C026D3')

$i = 0
foreach ($app in $Apps) {
    $color = $palette[$i % $palette.Count]
    $i++

    $btn = New-Object Windows.Controls.Button
    $btn.Width  = 200
    $btn.Height = 96
    $btn.Margin = '8'
    $btn.Cursor = 'Hand'
    $btn.BorderThickness = 0
    $btn.Background = (New-Object Windows.Media.BrushConverter).ConvertFromString($color)
    $btn.Foreground = 'White'
    $btn.Tag = $app

    # rounded corners via a control template
    $tpl = [xml]@"
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
  <Border CornerRadius="12" Background="{TemplateBinding Background}" Padding="14">
    <ContentPresenter VerticalAlignment="Center" HorizontalAlignment="Left"/>
  </Border>
</ControlTemplate>
"@
    $btn.Template = [Windows.Markup.XamlReader]::Load((New-Object System.Xml.XmlNodeReader $tpl))

    # tile content: app name + folder hint
    $stack = New-Object Windows.Controls.StackPanel
    $title = New-Object Windows.Controls.TextBlock
    $title.Text = $app.Name
    $title.FontSize = 17
    $title.FontWeight = 'Bold'
    $title.Foreground = 'White'
    $sub = New-Object Windows.Controls.TextBlock
    $sub.Text = ($app.Path -replace '^C:\\','')
    $sub.FontSize = 11
    $sub.Opacity = 0.85
    $sub.Margin = '0,6,0,0'
    $sub.Foreground = 'White'
    $sub.TextTrimming = 'CharacterEllipsis'
    $stack.AddChild($title)
    $stack.AddChild($sub)
    $btn.Content = $stack

    $btn.Add_Click({
        param($s, $e)
        try {
            Start-AppTile -App $s.Tag
        } catch {
            [Windows.MessageBox]::Show("Couldn't launch $($s.Tag.Name):`n$($_.Exception.Message)", 'Launch error')
        }
    })

    [void]$tiles.Children.Add($btn)
}

[void]$window.ShowDialog()
