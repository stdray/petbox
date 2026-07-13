import daisyui from "daisyui";

/** @type {import('tailwindcss').Config} */
export default {
	content: ["./Pages/**/*.cshtml", "./Views/**/*.cshtml", "./ts/**/*.ts"],
	theme: {
		extend: {},
	},
	plugins: [daisyui],
	daisyui: {
		// nord/retro (work `ui-theme-palette-expand`): the maintainer's two non-white light-theme
		// trial candidates, selectable side by side via /ui/me/preferences. CSS grows linearly
		// with the theme count — keep this set small; drop whichever loses once he's looked at
		// both live.
		themes: ["dark", "light", "nord", "retro"],
		darkTheme: "dark",
		logs: false,
	},
};
