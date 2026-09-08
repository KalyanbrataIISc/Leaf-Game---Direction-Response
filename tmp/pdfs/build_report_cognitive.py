from pathlib import Path
import math, shutil, sys
from reportlab.pdfgen import canvas
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle
from reportlab.platypus import Paragraph
from reportlab.lib.colors import HexColor, black, white
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from pypdf import PdfReader

root=Path.cwd()
out=root/'output'/'pdf'
tmp=root/'tmp'/'pdfs'
out.mkdir(parents=True,exist_ok=True)
tmp.mkdir(parents=True,exist_ok=True)
if len(sys.argv)>1:
    shutil.copy2(sys.argv[1],out/'closed_loop_board_illustration.png')
fonts=Path('C:/Windows/Fonts')
for name,file in [('Report','arial.ttf'),('ReportBold','arialbd.ttf'),('ReportItalic','ariali.ttf')]:
    pdfmetrics.registerFont(TTFont(name,str(fonts/file)))
pdfmetrics.registerFontFamily('Report',normal='Report',bold='ReportBold',italic='ReportItalic',boldItalic='ReportBold')
body=ParagraphStyle('Body',fontName='Report',fontSize=10.3,leading=13.5,textColor=black)
caption=ParagraphStyle('Caption',fontName='Report',fontSize=7.6,leading=9.5,textColor=HexColor('#39434A'))
source=ParagraphStyle('Source',fontName='Report',fontSize=7.2,leading=9.1,textColor=HexColor('#414A50'))
ink=HexColor('#243940')
green=HexColor('#267451')
def para(c,text,x,y,width,style=body):
    p=Paragraph(text,style);w,h=p.wrap(width,1000);p.drawOn(c,x,y-h)
    return y-h
def arrow(c,x1,y1,x2,y2,color=ink,width=1.3):
    c.setStrokeColor(color);c.setFillColor(color);c.setLineWidth(width);c.line(x1,y1,x2,y2)
    a=math.atan2(y2-y1,x2-x1)
    p=c.beginPath();p.moveTo(x2,y2)
    p.lineTo(x2-5.3*math.cos(a-0.45),y2-5.3*math.sin(a-0.45))
    p.lineTo(x2-5.3*math.cos(a+0.45),y2-5.3*math.sin(a+0.45));p.close()
    c.drawPath(p,stroke=0,fill=1)
def label(c,s,x,y,size=8,bold=False,color=ink):
    c.setFillColor(color);c.setFont('ReportBold' if bold else 'Report',size);c.drawCentredString(x,y,s)
def diagram(c,x,y,width):
    h=width*2/3
    c.drawImage(str(out/'closed_loop_board_illustration.png'),x,y,width=width,height=h,mask='auto')
    c.saveState();c.translate(x,y);c.scale(width/300,width/300)
    label(c,'Participant',48,199,7.5,True)
    label(c,'EEG acquisition and analysis',204,199,7.5,True)
    label(c,'Display-free board',204,188,7.3)
    label(c,'Tablet',191,5,7.5,True)
    arrow(c,115,164,177,164)
    label(c,'EEG',147,171,7.1)
    c.setStrokeColor(green);c.setLineWidth(1.3);c.line(242,132,242,80)
    arrow(c,242,80,215,80,green)
    label(c,'NF over',268,110,7.1,False,green)
    label(c,'Wi-Fi',268,100,7.1,False,green)
    arrow(c,136,98,99,124,green)
    label(c,'Visual feedback',139,119,7.1,False,green)
    c.restoreState()
    return h

W,H=A4;m=40;cw=W-2*m
c=canvas.Canvas(str(out/'Leaf_task_scientific_report.pdf'),pagesize=A4)
c.setTitle('SSVEP guided attention training with the Leaf task')
c.setAuthor('Scientific study overview')
c.setSubject('Selective attention training, feature-specific behavior and portable closed-loop EEG neurofeedback')
y=H-42
c.setFillColor(black);c.setFont('ReportBold',19);c.drawString(m,y,'SSVEP guided attention training')
y-=20;c.setFont('Report',11);c.drawString(m,y,'Feature-specific behavioral transfer in the Leaf task')
y-=23
y=para(c,'<b>Scientific rationale.</b> The Leaf paradigm investigates whether training selective attention with steady-state visual evoked potential (SSVEP) neurofeedback (NF) improves subsequent behavior, and whether the benefit differs between a leaf\'s <b>pointing direction</b> (orientation) and <b>movement direction</b> (motion). Training targets sustained selection of the cued group while resisting interference from competing leaves. Attention modulates frequency-tagged electroencephalographic (EEG) responses [1], providing an online marker of relative selection. The key question is whether learning to regulate this marker transfers to behavioral performance, either similarly across the two features or preferentially to one.',m,y,cw)
y-=14
left=307;gap=17;right=cw-left-gap
c.setFillColor(black);c.setFont('ReportBold',9)
c.drawString(m,y,'A   Portable closed loop')
c.drawString(m+left+gap,y,'B   Leaf task')
top=y-11
dh=diagram(c,m,top-left*2/3,left)
ih=right*551/1004
c.drawImage(str(out/'leaf_task_screencap.png'),m+left+gap,top-ih,width=right,height=ih)
para(c,'Unity demonstration screencap after color reveal. The blue and orange groups occupy the same display area. This Pointing cue requires the blue leaves\' pointing direction.',m+left+gap,top-ih-7,right,caption)
para(c,'<b>Sequence</b><br/>Attend to the cued group<br/>Achieve the NF criterion<br/>Perform the behavioral judgment',m+left+gap,top-ih-56,right,caption)
y=top-dh-4
y=para(c,'EEG is acquired and analyzed on one small board without a display. NF is transmitted to the game tablet over Wi-Fi in real time; visual feedback closes the attention-training loop. Apparatus illustration is schematic.',m,y,cw,caption)
y-=11
y=para(c,'<b>Attention training followed by behavioral testing.</b> Inspired by Lumosity\'s <i>Ebb and Flow</i> [2], the task presents intermingled leaf groups with independently assigned pointing and movement directions. Their outlines flicker at 19 and 23 Hz. A cue specifies the relevant group and the feature to judge. Participants must first attend to that group to raise the NF-controlled brightness. Sustaining the green criterion for 1 s reveals the blue and orange fills and enables a four-direction response within 4 s. Thus, neural selection precedes the behavioral judgment; accuracy and reaction time from reveal measure performance.',m,y,cw)
y-=8
y=para(c,'<b>SSVEP attention marker.</b> The supplied reference algorithm estimates normalized 19 and 23 Hz power from 1 s EEG windows at approximately 100 ms intervals. A cue-dependent log contrast between the two tags, adjusted against its within-trial history and smoothed, drives feedback. The board performs acquisition and analysis and sends the resulting NF value to the tablet. This marker indexes relative selection of the tagged groups; feature-specific benefit must be established from the subsequent pointing and movement responses.',m,y,cw)
y-=8
y=para(c,'<b>Planned evaluation.</b> Compare behavioral accuracy and correct-response latency after SSVEP-guided training with a no-training control matched for practice, stimulus exposure and reveal timing. A <b>training condition by judged feature interaction</b> tests whether the behavioral benefit differs between pointing and movement. NF attainment, time to criterion and SSVEP modulation provide complementary measures of attentional regulation; their relationship with behavioral gains tests neural-to-behavioral transfer. Counterbalancing tag/color mappings is needed to isolate feature effects. Control comparisons and separate neural and behavioral outcomes are essential [3]. Improvements, feature specificity and equivalence remain empirical questions; no participant results are reported here.',m,y,cw)
y-=10
y=para(c,'<b>Sources</b>  [1] <link href="https://doi.org/10.1073/pnas.0606668103" color="#264D62">Muller et al. (2006), <i>PNAS</i>, 103, 14250-14254</link>. '
    '[2] <link href="https://www.lumosity.com/en/brain-games/ebb-and-flow/" color="#264D62">Lumosity, Ebb and Flow</link>. '
    '[3] <link href="https://doi.org/10.1093/brain/awaa009" color="#264D62">Ros et al. (2020), CRED-nf, <i>Brain</i>, 143, 1674-1685</link>. '
    'Task and reference algorithm: Leaf project code, RT_acquisition_8.m and RT_experiment_5.m; portable apparatus as specified by the investigator.',m,y,cw,source)
c.showPage();c.save()
d=canvas.Canvas(str(out/'closed_loop_paradigm.pdf'),pagesize=(650,535))
d.setTitle('Portable closed loop SSVEP attention training')
d.setFillColor(black);d.setFont('ReportBold',16);d.drawString(25,505,'Portable closed loop SSVEP attention training')
diagram(d,25,70,600)
para(d,'EEG cap to display-free acquisition and analysis board; real-time NF over Wi-Fi to the tablet; visual feedback to the participant. Behavioral response follows successful attention training.',25,30,600,caption)
d.showPage();d.save()
reader=PdfReader(out/'Leaf_task_scientific_report.pdf')
print('Report pages:',len(reader.pages))
print('Final text bottom:',round(y,1))




