
const pngToIco = require("./node_modules/png-to-ico");
const fs = require("fs");
const files = ["D:\\Projects\\live-photo-box\\.tmp-ico\\icon-16.png", "D:\\Projects\\live-photo-box\\.tmp-ico\\icon-32.png", "D:\\Projects\\live-photo-box\\.tmp-ico\\icon-48.png", "D:\\Projects\\live-photo-box\\.tmp-ico\\icon-64.png", "D:\\Projects\\live-photo-box\\.tmp-ico\\icon-128.png", "D:\\Projects\\live-photo-box\\.tmp-ico\\icon-256.png"];
pngToIco(files).then(buf => {
    fs.writeFileSync("Live Photo Box/Assets/Icons/AppIcon.ico", buf);
    console.log("ICO written: " + buf.length + " bytes, " + files.length + " sizes");
}).catch(e => console.error("ERROR:", e));
