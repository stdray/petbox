import { buildOwnerOnlySkillsBlock } from "file:///D:/my/prj/petbox/src/clients-ts/petbox-wire/src/skill-files.ts";
const b = buildOwnerOnlySkillsBlock(process.argv[2], process.argv[3]);
process.stdout.write(b === null ? "<<NULL>>" : b);
