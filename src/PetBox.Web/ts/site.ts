import "htmx.org";
import Alpine from "alpinejs";

import "./logs";
import "./sidebar";
import { initBoardPage } from "./board";
import { initConfigPage } from "./config";
import { hydrateMarkdown } from "./markdown";
import { initWorkspacePersistence } from "./workspace";

Alpine.start();
initConfigPage();
initWorkspacePersistence();
initBoardPage();
hydrateMarkdown();
