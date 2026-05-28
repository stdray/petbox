declare module "alpinejs" {
	const Alpine: {
		start: () => void;
		data: (name: string, fn: () => Record<string, unknown>) => void;
	};
	export default Alpine;
}
