# PowerShell strips bare "--" before passing to $args; restore it so Godot's GetCmdlineUserArgs() works.
$a = $args | ForEach-Object { if ($_ -eq '--') { '--' } else { $_ } }
& "C:\Users\fbits\OneDrive\Desktop\Godot.NET\Godot_v4.7.2-stable_mono_win64_console.exe" $a
