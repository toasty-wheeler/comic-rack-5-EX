using System;

namespace cYo.Common.Windows.Forms.Theme;

public enum Themes
{
	Default,
	Dark,
	// Follows the Windows app mode (Settings > Personalization > Colors); resolved to Default or Dark at startup
	Auto
}
