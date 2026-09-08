from pathlib import Path
import ast, subprocess
from html.parser import HTMLParser
from docx import Document
from docx.shared import Pt, RGBColor
from docx.enum.section import WD_SECTION_START
from docx.enum.text import WD_BREAK, WD_LINE_SPACING
from docx.oxml import OxmlElement
from docx.oxml.ns import qn
from docx.opc.constants import RELATIONSHIP_TYPE as RT

root=Path.cwd(); scratch=root/'tmp'/'docx'; scratch.mkdir(parents=True,exist_ok=True)
output=root/'output'/'documents'; output.mkdir(parents=True,exist_ok=True)
pdfsource=(root/'tmp/pdfs/build_report_final.py').read_text(encoding='utf-8-sig')
env={}
exec(pdfsource.split('\nW,H=A4;')[0],env)
for name,w,h,func in [('loop_panel',289,204,'diagram'),('leaf_panel',515,89,'nf_progression')]:
    cv=env['canvas'].Canvas(str(scratch/(name+'.pdf')),pagesize=(w,h))
    env[func](cv,0,0,w)
    cv.showPage();cv.save()
    subprocess.run([str(Path('C:/Users/Kalyanbrata/.cache/codex-runtimes/codex-primary-runtime/dependencies/native/poppler/Library/bin/pdftoppm.exe')),'-r','250','-png','-singlefile',str(scratch/(name+'.pdf')),str(scratch/name)],check=True)
content=[]
for node in ast.walk(ast.parse(pdfsource)):
    if isinstance(node,ast.Call) and isinstance(node.func,ast.Name) and node.func.id=='para' and len(node.args)>1 and isinstance(node.args[0],ast.Name) and node.args[0].id=='c':
        content.append((node.lineno,ast.literal_eval(node.args[1])))
texts=[t for _,t in sorted(content)]
doc=Document()
sec=doc.sections[0]
sec.page_width=Pt(595.276);sec.page_height=Pt(841.89)
sec.left_margin=sec.right_margin=Pt(40)
sec.top_margin=Pt(24);sec.bottom_margin=Pt(29)
sec.header_distance=sec.footer_distance=Pt(10)
normal=doc.styles['Normal'];normal.font.name='Arial';normal.font.size=Pt(10.5);normal.font.color.rgb=RGBColor(0,0,0)
normal.paragraph_format.space_after=Pt(7);normal.paragraph_format.line_spacing=Pt(13.5)
normal.paragraph_format.widow_control=False
for sn in ['Title','Subtitle','Heading 1','Heading 2','Caption']:
    st=doc.styles[sn];st.font.name='Arial';st.font.color.rgb=RGBColor(0,0,0)
title=doc.add_paragraph('SSVEP guided attention training','Title')
title.runs[0].font.size=Pt(19);title.runs[0].bold=True
title.paragraph_format.line_spacing=Pt(23);title.paragraph_format.space_after=Pt(3)
sub=doc.add_paragraph('Behavioral transfer to pointing and movement judgments','Subtitle')
sub.runs[0].font.size=Pt(11)
sub.paragraph_format.line_spacing=Pt(14);sub.paragraph_format.space_after=Pt(16)

class Rich(HTMLParser):
    def __init__(self,p,size):
        super().__init__();self.p=p;self.size=size;self.bold=0;self.italic=0;self.href=None
    def handle_starttag(self,tag,attrs):
        if tag=='b':self.bold+=1
        elif tag=='i':self.italic+=1
        elif tag=='link':self.href=dict(attrs).get('href')
        elif tag=='br':self.p.add_run().add_break()
    def handle_endtag(self,tag):
        if tag=='b':self.bold-=1
        elif tag=='i':self.italic-=1
        elif tag=='link':self.href=None
    def handle_data(self,data):
        if not data:return
        if self.href:
            hyp=OxmlElement('w:hyperlink')
            hyp.set(qn('r:id'),self.p.part.relate_to(self.href,RT.HYPERLINK,is_external=True))
            rr=OxmlElement('w:r');pr=OxmlElement('w:rPr')
            fonts=OxmlElement('w:rFonts');fonts.set(qn('w:ascii'),'Arial');fonts.set(qn('w:hAnsi'),'Arial');pr.append(fonts)
            sz=OxmlElement('w:sz');sz.set(qn('w:val'),str(int(self.size*2)));pr.append(sz)
            color=OxmlElement('w:color');color.set(qn('w:val'),'264D62');pr.append(color)
            if self.bold:pr.append(OxmlElement('w:b'))
            if self.italic:pr.append(OxmlElement('w:i'))
            rr.append(pr);tt=OxmlElement('w:t');tt.set(qn('xml:space'),'preserve');tt.text=data;rr.append(tt);hyp.append(rr);self.p._p.append(hyp)
        else:
            r=self.p.add_run(data);r.font.size=Pt(self.size);r.bold=bool(self.bold);r.italic=bool(self.italic)
def para(text,size=10.5,leading=13.5,after=7):
    p=doc.add_paragraph();p.paragraph_format.line_spacing=Pt(leading);p.paragraph_format.space_after=Pt(after)
    p.paragraph_format.space_before=Pt(0);p.paragraph_format.widow_control=False
    Rich(p,size).feed(text)
    return p
def columns(section,widths):
    cols=section._sectPr.find(qn('w:cols'))
    if cols is None:cols=OxmlElement('w:cols');section._sectPr.append(cols)
    for child in list(cols):cols.remove(child)
    cols.set(qn('w:num'),str(len(widths)));cols.set(qn('w:space'),'340')
    cols.set(qn('w:equalWidth'),'0' if len(widths)>1 else '1')
    if len(widths)>1:
        for width in widths:
            col=OxmlElement('w:col');col.set(qn('w:w'),str(round(width*20)));col.set(qn('w:space'),'340');cols.append(col)
def picture(path,width):
    p=doc.add_paragraph();p.paragraph_format.space_after=Pt(0);p.paragraph_format.line_spacing=1
    p.add_run().add_picture(str(path),width=Pt(width))
    return p
para(texts[0],after=8)
two=doc.add_section(WD_SECTION_START.CONTINUOUS);columns(two,[289,209.276])
# A section-break paragraph contributes no visible extra space.
breakp=doc.paragraphs[-1];breakp.paragraph_format.line_spacing=Pt(1);breakp.paragraph_format.space_after=Pt(0)
para('<b>A   Closed loop attention training</b>',9,11,3)
picture(scratch/'loop_panel.png',289)
p=doc.add_paragraph();p.paragraph_format.space_after=Pt(0);p.paragraph_format.line_spacing=Pt(1);p.add_run().add_break(WD_BREAK.COLUMN)
para('<b>B   Behavioral task</b>',9,11,3)
picture(root/'output/pdf/leaf_task_screencap.png',209.276)
para(texts[1],7.5,9.5,0)
single=doc.add_section(WD_SECTION_START.CONTINUOUS);columns(single,[515.276])
breakp=doc.paragraphs[-1];breakp.paragraph_format.line_spacing=Pt(1);breakp.paragraph_format.space_after=Pt(0)
para(texts[2],7.5,9.5,7)
para('<b>C   Neurofeedback driven leaf fill</b>',9,11,0)
picture(scratch/'leaf_panel.png',515.276)
para(texts[3],7.5,9.5,9)
para(texts[4])
para(texts[5])
para(texts[6],after=9)
para(texts[7],7,9,0)
doc.core_properties.title='SSVEP guided attention training'
doc.core_properties.subject='Behavioral transfer to pointing and movement judgments'
doc.core_properties.author=''
path=output/'Leaf_task_scientific_report.docx'
for st in doc.styles:
    for node in list(st.element.iter()):
        if node.tag in (qn('w:pBdr'),qn('w:spacing')) and node.getparent() is not None:
            if node.tag==qn('w:pBdr') or node.getparent().tag==qn('w:rPr'):
                node.getparent().remove(node)
for p in doc.paragraphs:
    for node in list(p._p.iter(qn('w:pBdr'))):node.getparent().remove(node)
for p in [title,sub]:
    for r in p.runs:
        r.font.name='Arial';r.font.italic=False;r.font.color.rgb=RGBColor(0,0,0)
        pr=r._r.get_or_add_rPr()
        for tag in ['w:spacing','w:kern']:
            for node in list(pr.findall(qn(tag))):pr.remove(node)
        sp=OxmlElement('w:spacing');sp.set(qn('w:val'),'0');pr.append(sp)
        fonts=pr.find(qn('w:rFonts'))
        if fonts is not None:
            for key in list(fonts.attrib):
                if key.endswith('Theme'):del fonts.attrib[key]
doc.save(path)
print(path)
print('Text paragraphs:',len(doc.paragraphs))



