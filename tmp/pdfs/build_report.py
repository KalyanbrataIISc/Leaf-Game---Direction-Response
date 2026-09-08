from pathlib import Path
import shutil, math
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
shutil.copy2(root/'Temp'/'leaf_task_screencap.png',out/'leaf_task_screencap.png')
fonts=Path('C:/Windows/Fonts')
for name,file in [('Report','arial.ttf'),('ReportBold','arialbd.ttf'),('ReportItalic','ariali.ttf')]:
    pdfmetrics.registerFont(TTFont(name,str(fonts/file)))
pdfmetrics.registerFontFamily('Report',normal='Report',bold='ReportBold',italic='ReportItalic',boldItalic='ReportBold')
body=ParagraphStyle('Body',fontName='Report',fontSize=10.5,leading=14.0,textColor=black,spaceAfter=0)
caption=ParagraphStyle('Caption',fontName='Report',fontSize=7.9,leading=10.2,textColor=HexColor('#39434A'))
source=ParagraphStyle('Source',fontName='Report',fontSize=7.2,leading=9.1,textColor=HexColor('#4B5359'))
ink=HexColor('#243940')
green=HexColor('#28694F')
def para(c,text,x,y,width,style=body):
    p=Paragraph(text,style)
    w,h=p.wrap(width,1000)
    p.drawOn(c,x,y-h)
    return y-h
def arrow(c,x1,y1,x2,y2,color=ink):
    c.setStrokeColor(color);c.setFillColor(color);c.setLineWidth(1)
    c.line(x1,y1,x2,y2)
    a=math.atan2(y2-y1,x2-x1)
    p=c.beginPath();p.moveTo(x2,y2)
    p.lineTo(x2-5*math.cos(a-0.45),y2-5*math.sin(a-0.45))
    p.lineTo(x2-5*math.cos(a+0.45),y2-5*math.sin(a+0.45))
    p.close();c.drawPath(p,stroke=0,fill=1)
def label(c,s,x,y,size=7.7,bold=False,color=ink):
    c.setFillColor(color);c.setFont('ReportBold' if bold else 'Report',size);c.drawCentredString(x,y,s)
def box(c,x,y,w,h,title,lines,fill):
    c.setFillColor(HexColor(fill));c.setStrokeColor(HexColor('#B7C8C1'));c.setLineWidth(.6)
    c.roundRect(x,y,w,h,5,stroke=1,fill=1)
    label(c,title,x+w/2,y+h-13,8.5,True)
    for i,line in enumerate(lines): label(c,line,x+w/2,y+h-25-i*9.5,7.3)
def diagram(c,x,y,width=246,height=155):
    c.saveState();c.translate(x,y);c.scale(width/246,height/155)
    box(c,0,106,106,46,'Participant',['Attend to the cued','leaf group'],'#F2F5F3')
    box(c,140,106,106,46,'EEG acquisition',['41 channels at 128 Hz','FieldTrip buffer'],'#F2F5F3')
    box(c,140,32,106,48,'MATLAB feedback',['19 / 23 Hz spectral ratio','Cue-specific NF value'],'#EDF3F7')
    box(c,0,32,106,48,'Unity leaf display',['Brightness integration','Green hold and reveal'],'#E8F3EB')
    arrow(c,106,129,140,129)
    arrow(c,193,106,193,80)
    arrow(c,140,56,106,56)
    arrow(c,53,80,53,106,green)
    label(c,'visual',24,95,6.7,color=green)
    label(c,'feedback',24,85,6.7,color=green)
    arrow(c,53,32,53,15,green)
    c.setFillColor(HexColor('#E8F3EB'));c.setStrokeColor(HexColor('#B7C8C1'));c.roundRect(0,0,246,15,3,stroke=1,fill=1)
    label(c,'After reveal: arrow key or swipe; accuracy and RT',123,4.5,7.1)
    c.restoreState()

W,H=A4
c=canvas.Canvas(str(out/'Leaf_task_scientific_report.pdf'),pagesize=A4)
c.setTitle('The Leaf direction response task')
c.setAuthor('Scientific methods report')
c.setSubject('Source-based account of the Leaf task and SSVEP neurofeedback loop')
m=42;cw=W-2*m
y=H-43
c.setFillColor(black);c.setFont('ReportBold',19);c.drawString(m,y,'The Leaf direction response task')
y-=20
c.setFont('Report',11);c.drawString(m,y,'A closed loop SSVEP neurofeedback paradigm')
y-=16
c.setFont('Report',7.8);c.setFillColor(HexColor('#515B61'));c.drawString(m,y,'Scientific methods report  |  4 September 2026')
y-=17
y=para(c,'The Leaf task couples rule-guided visual selection with neurofeedback (NF) derived from steady-state visual evoked potentials (SSVEPs) in electroencephalography (EEG). It is designed to link attention to frequency-tagged leaf groups with contingent visual feedback and a subsequent four-choice direction response.',m,y,cw)
y-=17
col=(cw-18)/2
c.setFillColor(black);c.setFont('ReportBold',9.2)
c.drawString(m,y,'A   Task display')
c.drawString(m+col+18,y,'B   Closed loop paradigm')
top=y-9
imgH=col*551/1004
c.drawImage(str(out/'leaf_task_screencap.png'),m,top-imgH,width=col,height=imgH)
diagram(c,m+col+18,top-155,col,155)
para(c,'Demonstration screencap from the Unity renderer after color reveal. Blue leaves point right; the cue requires a right response. This frame is illustrative, without live EEG.',m,top-imgH-7,col,caption)
para(c,'The selected NF value changes the display, which provides visual feedback to the participant.',m+col+18,top-162,col,caption)
y=top-195
y=para(c,'<b>Task design.</b> Two interleaved groups of 10 leaves move continuously, with outlines luminance-modulated at 19 Hz (blue, C1) and 23 Hz (orange, C2). The cue instructs participants to report C1 pointing direction or C2 movement direction. Pointing and movement assignments use all four cardinal directions without repetition, yielding 24 configurations. Code defaults specify 24 trials per block with equally frequent, randomized cues. The pre-cue interval is 1 s plus a truncated exponential draw (scale 3 s, maximum 5 s); leaf interiors initially remain black.',m,y,cw)
y-=8
y=para(c,'<b>EEG acquisition and NF estimation.</b> The MATLAB scripts read 41 channels at 128 Hz from a FieldTrip buffer. Each estimate uses a 1 s window after three-sample lag differencing and common-average rereferencing, updated after at least 13 new samples (nominally about 102 ms). Chronux spectral estimates yield power at each tag divided by the mean power of its two neighboring frequency bins. These ratios are averaged across the same 28 selected electrodes for both tags. The SSVEP modulation index is <i>s</i> = ln(<i>A</i><sub>19</sub>) - ln(<i>A</i><sub>23</sub>); C2 uses its negative. After 11 estimates, the prior within-trial median (from estimate 11 onward) is subtracted. A gain of 0.2, saturation at ±1, and a five-estimate median produce the feedback; the initial 11 estimates are zero.',m,y,cw)
y-=8
y=para(c,'<b>Contingent display and response.</b> MATLAB writes two opposing NF values and a counter to binary <i>nf.txt</i>; Unity can read the file or receive its records through the supplied TCP relay. After cue onset, the cue-selected value is integrated into a shared brightness level, bounded from 0 to 1 (default rate 3 units per NF unit per second). Both groups turn green at a level of 0.9 or higher; maintaining this level for 1 s reveals their blue and orange fills. Participants then have 4 s to respond by arrow key or swipe; earlier responses are ignored. Failure to reveal within 10 s ends the trial. Answer feedback and the intertrial interval each last 1 s.',m,y,cw)
y-=8
y=para(c,'<b>Recorded measures and scope.</b> Trial records include accuracy, reaction time from color reveal, cue and reveal times, timeouts, NF traces, and missed display slots. Unity emits UDP event codes; MATLAB requires corresponding FieldTrip markers, so a marker bridge is needed for acquisition synchronization. This report describes the implementation and configurable code defaults; no participant outcomes or neurofeedback efficacy were assessed.',m,y,cw)
y-=12
y=para(c,'<b>Implementation sources</b>  LeafGameController.cs, LeafGameCore.cs, LeafVisualRenderer.cs and Tools/nf_tcp_server.py (Leaf project); RT_acquisition_8.m and RT_experiment_5.m (user-specified FeatureAttention files).',m,y,cw,source)
c.showPage();c.save()
d=canvas.Canvas(str(tmp/'closed_loop_paradigm.pdf'),pagesize=(510,340))
d.setTitle('Leaf task closed loop paradigm')
diagram(d,15,15,480,302)
d.showPage();d.save()
reader=PdfReader(out/'Leaf_task_scientific_report.pdf')
print('Report pages:',len(reader.pages))
print('Final text bottom (points):',round(y,1))
print('Output:',out/'Leaf_task_scientific_report.pdf')



