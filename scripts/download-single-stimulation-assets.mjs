import { mkdir, writeFile } from 'node:fs/promises';
const assets = {
  "075ef.svg": "https://www.figma.com/api/mcp/asset/dd36e5ea-8d91-4bdc-8a6b-d5f0cb048c00/075ef.svg",
  "4aed1.svg": "https://www.figma.com/api/mcp/asset/dd36e5ea-8d91-4bdc-8a6b-d5f0cb048c00/4aed1.svg",
  "daff8.svg": "https://www.figma.com/api/mcp/asset/dd36e5ea-8d91-4bdc-8a6b-d5f0cb048c00/daff8.svg",
  "a0cfe.svg": "https://www.figma.com/api/mcp/asset/dd36e5ea-8d91-4bdc-8a6b-d5f0cb048c00/a0cfe.svg",
  "fa628.svg": "https://www.figma.com/api/mcp/asset/dd36e5ea-8d91-4bdc-8a6b-d5f0cb048c00/fa628.svg",
  "256ff.svg": "https://www.figma.com/api/mcp/asset/dd36e5ea-8d91-4bdc-8a6b-d5f0cb048c00/256ff.svg",
  "53533.svg": "https://www.figma.com/api/mcp/asset/dd36e5ea-8d91-4bdc-8a6b-d5f0cb048c00/53533.svg",
  "8d643.svg": "https://www.figma.com/api/mcp/asset/a0d92dd0-472c-4806-a495-74b3545d6e8c/8d643.svg",
  "1b19f.png": "https://www.figma.com/api/mcp/asset/a0d92dd0-472c-4806-a495-74b3545d6e8c/1b19f.png",
  "7127a.png": "https://www.figma.com/api/mcp/asset/a0d92dd0-472c-4806-a495-74b3545d6e8c/7127a.png",
  "52cf8.png": "https://www.figma.com/api/mcp/asset/7064a435-5c72-4b34-bd7a-c3818bc890ca/52cf8.png",
  "79e96.svg": "https://www.figma.com/api/mcp/asset/a0d92dd0-472c-4806-a495-74b3545d6e8c/79e96.svg",
  "d0bd1.svg": "https://www.figma.com/api/mcp/asset/a0d92dd0-472c-4806-a495-74b3545d6e8c/d0bd1.svg",
  "b4152.svg": "https://www.figma.com/api/mcp/asset/a0d92dd0-472c-4806-a495-74b3545d6e8c/b4152.svg",
  "f57ca.svg": "https://www.figma.com/api/mcp/asset/a0d92dd0-472c-4806-a495-74b3545d6e8c/f57ca.svg",
  "5b2d9.svg": "https://www.figma.com/api/mcp/asset/a0d92dd0-472c-4806-a495-74b3545d6e8c/5b2d9.svg",
  "344db.svg": "https://www.figma.com/api/mcp/asset/a0d92dd0-472c-4806-a495-74b3545d6e8c/344db.svg",
  "546a4.svg": "https://www.figma.com/api/mcp/asset/dd36e5ea-8d91-4bdc-8a6b-d5f0cb048c00/546a4.svg",
  "eb10d.svg": "https://www.figma.com/api/mcp/asset/dd36e5ea-8d91-4bdc-8a6b-d5f0cb048c00/eb10d.svg",
  "3b1c7.svg": "https://www.figma.com/api/mcp/asset/a0d92dd0-472c-4806-a495-74b3545d6e8c/3b1c7.svg",
  "0b823.svg": "https://www.figma.com/api/mcp/asset/a0d92dd0-472c-4806-a495-74b3545d6e8c/0b823.svg",
  "6cb82.svg": "https://www.figma.com/api/mcp/asset/a0d92dd0-472c-4806-a495-74b3545d6e8c/6cb82.svg",
  "15bd5.svg": "https://www.figma.com/api/mcp/asset/7064a435-5c72-4b34-bd7a-c3818bc890ca/15bd5.svg",
  "26439.svg": "https://www.figma.com/api/mcp/asset/a0d92dd0-472c-4806-a495-74b3545d6e8c/26439.svg",
  "8bb34.svg": "https://www.figma.com/api/mcp/asset/7064a435-5c72-4b34-bd7a-c3818bc890ca/8bb34.svg",
  "<asset file name>": "https://www.figma.com/api/mcp/asset/dd36e5ea-8d91-4bdc-8a6b-d5f0cb048c00/<asset file name>",
  "3f7f6.svg": "https://www.figma.com/api/mcp/asset/a0d92dd0-472c-4806-a495-74b3545d6e8c/3f7f6.svg",
  "8d52e.svg": "https://www.figma.com/api/mcp/asset/a0d92dd0-472c-4806-a495-74b3545d6e8c/8d52e.svg",
  "7021f.svg": "https://www.figma.com/api/mcp/asset/a0d92dd0-472c-4806-a495-74b3545d6e8c/7021f.svg",
  "468a9.svg": "https://www.figma.com/api/mcp/asset/a0d92dd0-472c-4806-a495-74b3545d6e8c/468a9.svg",
  "f5683.svg": "https://www.figma.com/api/mcp/asset/a0d92dd0-472c-4806-a495-74b3545d6e8c/f5683.svg",
  "3d20c.svg": "https://www.figma.com/api/mcp/asset/dd36e5ea-8d91-4bdc-8a6b-d5f0cb048c00/3d20c.svg",
  "85551.svg": "https://www.figma.com/api/mcp/asset/dd36e5ea-8d91-4bdc-8a6b-d5f0cb048c00/85551.svg",
  "9ed52.svg": "https://www.figma.com/api/mcp/asset/dd36e5ea-8d91-4bdc-8a6b-d5f0cb048c00/9ed52.svg",
  "5f001.svg": "https://www.figma.com/api/mcp/asset/dd36e5ea-8d91-4bdc-8a6b-d5f0cb048c00/5f001.svg",
  "10430.svg": "https://www.figma.com/api/mcp/asset/dd36e5ea-8d91-4bdc-8a6b-d5f0cb048c00/10430.svg",
  "e567d.svg": "https://www.figma.com/api/mcp/asset/dd36e5ea-8d91-4bdc-8a6b-d5f0cb048c00/e567d.svg",
  "3b905.svg": "https://www.figma.com/api/mcp/asset/dd36e5ea-8d91-4bdc-8a6b-d5f0cb048c00/3b905.svg",
  "e4a30.svg": "https://www.figma.com/api/mcp/asset/dd36e5ea-8d91-4bdc-8a6b-d5f0cb048c00/e4a30.svg",
  "b18b3.svg": "https://www.figma.com/api/mcp/asset/dd36e5ea-8d91-4bdc-8a6b-d5f0cb048c00/b18b3.svg",
  "3ce42.svg": "https://www.figma.com/api/mcp/asset/dd36e5ea-8d91-4bdc-8a6b-d5f0cb048c00/3ce42.svg",
  "db61e.svg": "https://www.figma.com/api/mcp/asset/dd36e5ea-8d91-4bdc-8a6b-d5f0cb048c00/db61e.svg",
  "b6401.svg": "https://www.figma.com/api/mcp/asset/0cae7d1c-0c58-472f-b48e-661eaf7cf17b/b6401.svg",
  "a5122.svg": "https://www.figma.com/api/mcp/asset/dd36e5ea-8d91-4bdc-8a6b-d5f0cb048c00/a5122.svg",
  "4ae5d.svg": "https://www.figma.com/api/mcp/asset/dd36e5ea-8d91-4bdc-8a6b-d5f0cb048c00/4ae5d.svg",
  "69151.svg": "https://www.figma.com/api/mcp/asset/dd36e5ea-8d91-4bdc-8a6b-d5f0cb048c00/69151.svg",
  "08ec1.svg": "https://www.figma.com/api/mcp/asset/dd36e5ea-8d91-4bdc-8a6b-d5f0cb048c00/08ec1.svg",
  "73f26.svg": "https://www.figma.com/api/mcp/asset/0cae7d1c-0c58-472f-b48e-661eaf7cf17b/73f26.svg",
  "b51b1.svg": "https://www.figma.com/api/mcp/asset/0cae7d1c-0c58-472f-b48e-661eaf7cf17b/b51b1.svg",
  "ad98f.svg": "https://www.figma.com/api/mcp/asset/dd36e5ea-8d91-4bdc-8a6b-d5f0cb048c00/ad98f.svg",
  "5c4d6.svg": "https://www.figma.com/api/mcp/asset/dd36e5ea-8d91-4bdc-8a6b-d5f0cb048c00/5c4d6.svg",
  "f8d18.svg": "https://www.figma.com/api/mcp/asset/dd36e5ea-8d91-4bdc-8a6b-d5f0cb048c00/f8d18.svg",
  "03a27.svg": "https://www.figma.com/api/mcp/asset/dd36e5ea-8d91-4bdc-8a6b-d5f0cb048c00/03a27.svg",
  "0f972.svg": "https://www.figma.com/api/mcp/asset/dd36e5ea-8d91-4bdc-8a6b-d5f0cb048c00/0f972.svg",
  "6e4f0.svg": "https://www.figma.com/api/mcp/asset/dd36e5ea-8d91-4bdc-8a6b-d5f0cb048c00/6e4f0.svg",
  "9467c.svg": "https://www.figma.com/api/mcp/asset/dd36e5ea-8d91-4bdc-8a6b-d5f0cb048c00/9467c.svg",
  "29241.svg": "https://www.figma.com/api/mcp/asset/8c5ba293-d1eb-4e9b-9acf-a241d8aafcbb/29241.svg",
  "e6471.svg": "https://www.figma.com/api/mcp/asset/8c5ba293-d1eb-4e9b-9acf-a241d8aafcbb/e6471.svg",
  "54876.svg": "https://www.figma.com/api/mcp/asset/dd36e5ea-8d91-4bdc-8a6b-d5f0cb048c00/54876.svg",
  "14529.svg": "https://www.figma.com/api/mcp/asset/dd36e5ea-8d91-4bdc-8a6b-d5f0cb048c00/14529.svg",
  "5e139.svg": "https://www.figma.com/api/mcp/asset/766041d5-e753-4dc3-99e8-2272c1f37a4a/5e139.svg"
};
await mkdir('public/assets/single-stimulation', { recursive: true });
const requested = new Set(process.argv.slice(2));
for (const [name, url] of Object.entries(assets)) {
 if (name.includes('<')) continue;
 if (requested.size && !requested.has(name)) continue;
 const response = await fetch(url);
 if (!response.ok) throw new Error(name + ': ' + response.status);
 await writeFile('public/assets/single-stimulation/' + name, Buffer.from(await response.arrayBuffer()));
 console.log(name);
}
