import daisyui from "daisyui";

/** @type {import('tailwindcss').Config} */
export default {
	content: ["./Pages/**/*.cshtml", "./Views/**/*.cshtml", "./ts/**/*.ts"],
	theme: {
		extend: {},
	},
	plugins: [daisyui],
	daisyui: {
		themes: ["dark", "night", "business"],
		darkTheme: "dark",
		logs: false,
	},
};
