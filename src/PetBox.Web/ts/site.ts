import "htmx.org";
import Alpine from "alpinejs";

import "./logs";
import "./sidebar";
import { initConfigPage } from "./config";
import { initWorkspacePersistence } from "./workspace";

Alpine.start();
initConfigPage();
initWorkspacePersistence();
