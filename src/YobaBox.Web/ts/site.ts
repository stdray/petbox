import "htmx.org";
import Alpine from "alpinejs";

import "./logs";

Alpine.start();

document.addEventListener("alpine:init", () => {
	// Sidebar mobile toggle and other Alpine components will be registered here.
});
